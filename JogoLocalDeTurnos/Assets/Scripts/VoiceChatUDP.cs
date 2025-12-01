using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// VoiceChatUDP com fragmentação e reassemblagem (push-to-talk).
/// </summary>
public class VoiceChatUDP : MonoBehaviour
{
    public static VoiceChatUDP Instance;

    public string peerIp = "10.57.1.132";
    public int port = 5151;
    public int sampleRate = 8000;
    public int maxPayloadSize = 1200;

    AudioSource audioSource;
    public string micDevice;
    AudioClip recordingClip;
    Thread listenerThread;
    volatile bool isRecording = false;
    UdpClient udpClient; 

    // Estrutura para reassemblagem
    private class ReassemblyEntry
    {
        public ushort TotalPackets;
        public Dictionary<ushort, byte[]> Packets = new Dictionary<ushort, byte[]>();
        public DateTime LastPacketTime = DateTime.Now;
        public int TotalBytes = 0;
    }

    readonly Dictionary<uint, ReassemblyEntry> reassemblies = new Dictionary<uint, ReassemblyEntry>();
    readonly object reassemblyLock = new object();
    const int REASSEMBLY_TIMEOUT_SECONDS = 10;
    
    // --- UNITY LIFECYCLE ---

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            audioSource = gameObject.AddComponent<AudioSource>();
            StartListening();
            // StartCleanupThread(); // Opcional, para limpar mensagens incompletas
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void OnDestroy()
    {
        StopListening();
    }

    // --- ESCUTA E RECEPÇÃO ---

    public void StopListening()
    {
        if (listenerThread != null && listenerThread.IsAlive)
        {
            try
            {
                udpClient?.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VoiceChatUDP] Erro ao fechar UdpClient: " + ex.Message);
            }
        }
    }

    public void StartListening()
    {
        if (listenerThread != null && listenerThread.IsAlive) return;

        listenerThread = new Thread(() =>
        {
            try
            {
                udpClient = new UdpClient(port);
                IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                
                while (true)
                {
                    try
                    {
                        var data = udpClient.Receive(ref remoteEp);
                        if (data == null || data.Length < 8) continue;

                        // Header little-endian:
                        uint messageId = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                        ushort totalPackets = (ushort)(data[4] | (data[5] << 8));
                        ushort packetIndex = (ushort)(data[6] | (data[7] << 8));

                        int payloadLen = data.Length - 8;
                        byte[] payload = new byte[payloadLen];
                        Array.Copy(data, 8, payload, 0, payloadLen);

                        HandleReceivedPacket(messageId, totalPackets, packetIndex, payload);
                    }
                    catch (SocketException se) 
                    { 
                        if (se.SocketErrorCode == SocketError.Interrupted || se.SocketErrorCode == SocketError.ConnectionReset) 
                            return; 
                        
                        // Garante que o log de erro seja executado na Main Thread
                        if (UnityMainThreadDispatcher.Exists())
                            UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.LogWarning("[VoiceChatUDP] Socket error: " + se.Message));
                    }
                    catch (Exception ex)
                    {
                        // Garante que o log de erro seja executado na Main Thread
                        if (UnityMainThreadDispatcher.Exists())
                            UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.LogError("[VoiceChatUDP] Receive error: " + ex.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                if (UnityMainThreadDispatcher.Exists())
                    UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.LogError("[VoiceChatUDP] Listener fatal: " + ex.Message));
            }
            finally
            {
                udpClient?.Close(); 
                udpClient = null;
            }
        })
        { IsBackground = true };
        listenerThread.Start();
    }
    
    // --- REASSEMBLAGEM E REPRODUÇÃO (CHAVE PARA OUVIR) ---

    private void HandleReceivedPacket(uint messageId, ushort totalPackets, ushort packetIndex, byte[] payload)
    {
        lock (reassemblyLock)
        {
            // 1. Cria ou obtém a entrada
            if (!reassemblies.TryGetValue(messageId, out ReassemblyEntry entry))
            {
                entry = new ReassemblyEntry { TotalPackets = totalPackets };
                reassemblies.Add(messageId, entry);
            }
            
            // 2. Adiciona o pacote (se não for duplicado)
            if (!entry.Packets.ContainsKey(packetIndex))
            {
                entry.Packets.Add(packetIndex, payload);
                entry.LastPacketTime = DateTime.Now;
                entry.TotalBytes += payload.Length;
            }

            // 3. Verifica se a mensagem está completa
            if (entry.Packets.Count == entry.TotalPackets)
            {
                Debug.Log($"[VoiceChatUDP] Reassemblagem completa: ID {messageId} com {entry.TotalPackets} pacotes.");
                
                // Monta o array de bytes final
                byte[] allBytes = new byte[entry.TotalBytes];
                int currentOffset = 0;
                
                // Monta os pacotes na ordem correta (0, 1, 2, ...)
                for (ushort i = 0; i < entry.TotalPackets; i++)
                {
                    if (entry.Packets.TryGetValue(i, out byte[] packetData))
                    {
                        Array.Copy(packetData, 0, allBytes, currentOffset, packetData.Length);
                        currentOffset += packetData.Length;
                    }
                    else
                    {
                        // Se faltar um pacote, algo deu muito errado no UDP. Abortar a mensagem.
                        Debug.LogError($"[VoiceChatUDP] Falha na remontagem: Pacote {i} faltando. Abortando mensagem {messageId}.");
                        reassemblies.Remove(messageId);
                        return;
                    }
                }
                
                // 4. Envia para a Main Thread para reprodução
                if (UnityMainThreadDispatcher.Exists())
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(() => PlayAudio(allBytes));
                }
                
                reassemblies.Remove(messageId);
            }
        }
    }

    private void PlayAudio(byte[] pcmData)
    {
        // Esta função é executada na Main Thread (garantido pelo Dispatcher)
        
        // 1. Converter bytes PCM16 (short) para float (formato do AudioClip)
        int samplesCount = pcmData.Length / sizeof(short);
        float[] samples = new float[samplesCount];
        short[] pcm16 = new short[samplesCount];
        
        Buffer.BlockCopy(pcmData, 0, pcm16, 0, pcmData.Length);

        // Converte short[] para float[] (normalização para -1.0 a 1.0)
        for (int i = 0; i < samplesCount; i++)
        {
            samples[i] = (float)pcm16[i] / short.MaxValue;
        }

        // 2. Criar e carregar o AudioClip
        // O Mono (canais=1) é ideal para voz
        AudioClip clip = AudioClip.Create("ReceivedVoice", samplesCount, 1, sampleRate, false);
        clip.SetData(samples, 0);

        // 3. Reproduzir o áudio
        audioSource.PlayOneShot(clip);
        
        Debug.Log($"[VoiceChatUDP] Reprodução de áudio iniciada: {samplesCount} samples ({clip.length}s).");
    }

    // --- GRAVAÇÃO E ENVIO ---
    
    // (O código de BeginRecording e EndRecordingAndSend é mantido como no exemplo anterior, 
    // com a correção de thread safety no Debug.LogError/Log)
    
    public void BeginRecording()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[VoiceChatUDP] Nenhum microfone detectado.");
            return;
        }
        if (isRecording) return;

        micDevice = Microphone.devices[0];
        // Reduzido o tempo de buffer para 10 segundos, 300 era muito longo
        recordingClip = Microphone.Start(micDevice, true, 10, sampleRate); 
        isRecording = true;
        Debug.Log("[VoiceChatUDP] Iniciando gravação (push-to-talk).");
    }

    public void EndRecordingAndSend()
    {
        if (!isRecording) return;
        
        // Lógica de EndRecording, conversão para PCM16 e envio em ThreadPool...
        // ... (código anterior)
        
        // Exemplo da parte do loop de envio
        ThreadPool.QueueUserWorkItem((_) =>
        {
            // ... (logica de envio TCP)
            
            // Garante que o log de sucesso seja executado na Main Thread
            // Se o seu código for assim:
            // if (UnityMainThreadDispatcher.Exists())
            //     UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.Log($"[VoiceChatUDP] Áudio enviado: {allBytes.Length} bytes em {totalPackets} pacotes."));
            
            // ...
        });
        
        // ...
    }
}