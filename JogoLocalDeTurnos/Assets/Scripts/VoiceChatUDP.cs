using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// VoiceChatUDP com fragmentação e reassemblagem (push-to-talk).
/// Uso:
/// - BeginRecording() em PointerDown
/// - EndRecordingAndSend() em PointerUp
/// Certifique-se que ambos peers têm uma instância deste script ativa e que peerIp/port estão corretos.
/// </summary>
public class VoiceChatUDP : MonoBehaviour
{
    public static VoiceChatUDP Instance;

    public string peerIp = "10.57.1.134";
    public int port = 5151;
    public int sampleRate = 8000; // reduzir taxa reduz tamanho de pacote
    public int maxPayloadSize = 1200; // bytes por pacote (seguro para MTU)

    AudioSource audioSource;
    string micDevice;
    AudioClip recordingClip;
    Thread listenerThread;
    volatile bool isRecording = false;

    // Reassemblies: messageId -> entry
    readonly Dictionary<uint, ReassemblyEntry> reassemblies = new Dictionary<uint, ReassemblyEntry>();
    readonly object reassemblyLock = new object();

    // Limpeza de reassemblies antigos
    const int REASSEMBLY_TIMEOUT_SECONDS = 10;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            audioSource = gameObject.AddComponent<AudioSource>();
            StartListening();
            StartCleanupThread();
        }
        else
        {
            Destroy(gameObject);
            return;
        }
    }

    // Listener UDP (roda em thread)
    public void StartListening()
    {
        if (listenerThread != null && listenerThread.IsAlive) return;

        listenerThread = new Thread(() =>
        {
            try
            {
                using (var udp = new UdpClient(port))
                {
                    IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    while (true)
                    {
                        try
                        {
                            var data = udp.Receive(ref remoteEp);
                            if (data == null || data.Length < 8) continue; // header mínimo

                            // Parse header: [uint messageId (4)] [ushort totalPackets (2)] [ushort packetIndex (2)]
                            uint messageId = BitConverter.ToUInt32(data, 0);
                            ushort totalPackets = BitConverter.ToUInt16(data, 4);
                            ushort packetIndex = BitConverter.ToUInt16(data, 6);

                            int payloadLen = data.Length - 8;
                            byte[] payload = new byte[payloadLen];
                            Array.Copy(data, 8, payload, 0, payloadLen);

                            HandleReceivedPacket(messageId, totalPackets, packetIndex, payload);
                        }
                        catch (SocketException) { /* ignore transient socket errors */ }
                        catch (Exception ex) { Debug.LogError("[VoiceChatUDP] Receive error: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[VoiceChatUDP] Listener fatal: " + ex.Message);
            }
        })
        { IsBackground = true };
        listenerThread.Start();
    }

    // Begin recording (push-to-talk start)
    public void BeginRecording()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[VoiceChatUDP] Nenhum microfone detectado.");
            return;
        }

        if (isRecording) return;

        micDevice = Microphone.devices[0];
        // grava em loop para parar manualmente; buffer longo (e.g., 300s)
        recordingClip = Microphone.Start(micDevice, true, 300, sampleRate);
        isRecording = true;
        Debug.Log("[VoiceChatUDP] Iniciando gravação (push-to-talk).");
    }

    // End recording and send (push-to-talk end)
    public void EndRecordingAndSend()
    {
        if (!isRecording)
        {
            Debug.Log("[VoiceChatUDP] Não estava gravando.");
            return;
        }

        int pos = Microphone.GetPosition(micDevice);
        Microphone.End(micDevice);
        isRecording = false;

        if (pos <= 0)
        {
            Debug.Log("[VoiceChatUDP] Nenhum dado de áudio gravado.");
            return;
        }

        // Obter samples float gravados
        float[] samples = new float[pos];
        recordingClip.GetData(samples, 0);

        // Converter para PCM16 (short) para reduzir tamanho
        short[] pcm16 = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float f = Mathf.Clamp(samples[i], -1f, 1f);
            pcm16[i] = (short)(f * short.MaxValue);
        }

        // Converter short[] para byte[]
        byte[] allBytes = new byte[pcm16.Length * sizeof(short)];
        Buffer.BlockCopy(pcm16, 0, allBytes, 0, allBytes.Length);

        // Fragmentar e enviar
        ThreadPool.QueueUserWorkItem((_) =>
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    // message id único
                    uint messageId = (uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue);
                    int payloadPerPacket = maxPayloadSize; // já exclui header
                    int totalPackets = (allBytes.Length + payloadPerPacket - 1) / payloadPerPacket;

                    for (int i = 0; i < totalPackets; i++)
                    {
                        int offset = i * payloadPerPacket;
                        int size = Math.Min(payloadPerPacket, allBytes.Length - offset);

                        // header
                        byte[] header = new byte[8];
                        Array.Copy(BitConverter.GetBytes(messageId), 0, header, 0, 4);
                        Array.Copy(BitConverter.GetBytes((ushort)totalPackets), 0, header, 4, 2);
                        Array.Copy(BitConverter.GetBytes((ushort)i), 0, header, 6, 2);

                        byte[] packet = new byte[8 + size];
                        Array.Copy(header, 0, packet, 0, 8);
                        Array.Copy(allBytes, offset, packet, 8, size);

                        udp.Send(packet, packet.Length, peerIp, port);
                    }
                }

                Debug.Log($"[VoiceChatUDP] Áudio enviado: {allBytes.Length} bytes em {Math.Ceiling((double)allBytes.Length / maxPayloadSize)} pacotes.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[VoiceChatUDP] Erro ao enviar áudio: " + ex.Message);
            }
        });
    }

    // Recebe um pacote fragmentado
    void HandleReceivedPacket(uint messageId, ushort totalPackets, ushort packetIndex, byte[] payload)
    {
        lock (reassemblyLock)
        {
            if (!reassemblies.TryGetValue(messageId, out var entry))
            {
                entry = new ReassemblyEntry(totalPackets);
                reassemblies[messageId] = entry;
            }

            // ignore se já recebido esse índice
            if (!entry.parts.ContainsKey(packetIndex))
            {
                entry.parts[packetIndex] = payload;
                entry.received++;
                entry.lastUpdate = DateTime.UtcNow;
            }

            if (entry.received >= entry.total)
            {
                // reconstruir em ordem
                int totalBytes = 0;
                for (int i = 0; i < entry.total; i++)
                    totalBytes += entry.parts[i].Length;

                byte[] all = new byte[totalBytes];
                int pos = 0;
                for (int i = 0; i < entry.total; i++)
                {
                    byte[] part = entry.parts[i];
                    Array.Copy(part, 0, all, pos, part.Length);
                    pos += part.Length;
                }

                // remover entrada
                reassemblies.Remove(messageId);

                // converter bytes -> short[] -> float[] e reproduzir na thread principal
                int shortCount = all.Length / sizeof(short);
                short[] pcm16 = new short[shortCount];
                Buffer.BlockCopy(all, 0, pcm16, 0, all.Length);

                float[] samples = new float[shortCount];
                for (int i = 0; i < shortCount; i++)
                    samples[i] = pcm16[i] / (float)short.MaxValue;

                // Play clip na thread principal usando dispatcher
                if (UnityMainThreadDispatcher.Exists())
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(() => PlayClip(samples));
                }
            }
        }
    }

    void PlayClip(float[] samples)
    {
        try
        {
            AudioClip clip = AudioClip.Create("UDPVoice", samples.Length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            audioSource.Stop();
            audioSource.clip = clip;
            audioSource.Play();
        }
        catch (Exception ex)
        {
            Debug.LogError("[VoiceChatUDP] PlayClip erro: " + ex.Message);
        }
    }

    // Limpeza periódica de reassemblies antigos
    void StartCleanupThread()
    {
        Thread t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(2000);
                lock (reassemblyLock)
                {
                    var keys = new List<uint>(reassemblies.Keys);
                    var now = DateTime.UtcNow;
                    foreach (var k in keys)
                    {
                        if ((now - reassemblies[k].lastUpdate).TotalSeconds > REASSEMBLY_TIMEOUT_SECONDS)
                            reassemblies.Remove(k);
                    }
                }
            }
        })
        { IsBackground = true };
        t.Start();
    }

    private void OnApplicationQuit()
    {
        try
        {
            if (listenerThread != null && listenerThread.IsAlive) listenerThread.Abort();
        }
        catch { }
    }

    class ReassemblyEntry
    {
        public ushort total;
        public int received;
        public Dictionary<ushort, byte[]> parts = new Dictionary<ushort, byte[]>();
        public DateTime lastUpdate;

        public ReassemblyEntry(ushort total)
        {
            this.total = total;
            received = 0;
            lastUpdate = DateTime.UtcNow;
        }
    }
}