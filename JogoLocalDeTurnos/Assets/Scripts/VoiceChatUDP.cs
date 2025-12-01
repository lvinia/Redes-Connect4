using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// VoiceChatUDP com fragmentação e reassemblagem (push-to-talk).
/// - BeginRecording() em PointerDown
/// - EndRecordingAndSend() em PointerUp
/// </summary>
public class VoiceChatUDP : MonoBehaviour
{
    public static VoiceChatUDP Instance;

    public string peerIp = "10.57.1.134";
    public int port = 5151;
    public int sampleRate = 8000;       // taxa de amostragem (reduz tamanho)
    public int maxPayloadSize = 1200;   // bytes por pacote (payload seguro)

    AudioSource audioSource;
    string micDevice;
    AudioClip recordingClip;
    Thread listenerThread;
    volatile bool isRecording = false;

    readonly Dictionary<uint, ReassemblyEntry> reassemblies = new Dictionary<uint, ReassemblyEntry>();
    readonly object reassemblyLock = new object();
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

    // Inicia listener UDP em background
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
                            if (data == null || data.Length < 8) continue;

                            // header little-endian:
                            uint messageId = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                            ushort totalPackets = (ushort)(data[4] | (data[5] << 8));
                            ushort packetIndex = (ushort)(data[6] | (data[7] << 8));

                            int payloadLen = data.Length - 8;
                            byte[] payload = new byte[payloadLen];
                            Array.Copy(data, 8, payload, 0, payloadLen);

                            HandleReceivedPacket(messageId, totalPackets, packetIndex, payload);
                        }
                        catch (SocketException) { /* ignore transient socket errors */ }
                        catch (Exception ex)
                        {
                            if (UnityMainThreadDispatcher.Exists())
                                UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.LogError("[VoiceChatUDP] Receive error: " + ex.Message));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (UnityMainThreadDispatcher.Exists())
                    UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.LogError("[VoiceChatUDP] Listener fatal: " + ex.Message));
            }
        })
        { IsBackground = true };
        listenerThread.Start();
    }

    // Inicia gravação contínua (será parada manualmente)
    public void BeginRecording()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[VoiceChatUDP] Nenhum microfone detectado.");
            return;
        }
        if (isRecording) return;

        micDevice = Microphone.devices[0];
        recordingClip = Microphone.Start(micDevice, true, 300, sampleRate);
        isRecording = true;
        Debug.Log("[VoiceChatUDP] Iniciando gravação (push-to-talk).");
    }

    // Para gravação e faz envio fragmentado (executado em thread pool)
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

        // pega samples float
        float[] samples = new float[pos];
        recordingClip.GetData(samples, 0);

        // converte para PCM16 (short)
        short[] pcm16 = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float f = Mathf.Clamp(samples[i], -1f, 1f);
            pcm16[i] = (short)(f * short.MaxValue);
        }

        // bytes totais
        byte[] allBytes = new byte[pcm16.Length * sizeof(short)];
        Buffer.BlockCopy(pcm16, 0, allBytes, 0, allBytes.Length);

        // Gera messageId sem usar UnityEngine.Random (main-thread-safe)
        uint messageId;
        {
            var g = Guid.NewGuid().ToByteArray();
            messageId = BitConverter.ToUInt32(g, 0);
        }

        ThreadPool.QueueUserWorkItem((_) =>
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    int payloadPerPacket = maxPayloadSize;
                    int totalPacketsInt = (allBytes.Length + payloadPerPacket - 1) / payloadPerPacket;

                    if (totalPacketsInt > ushort.MaxValue)
                    {
                        if (UnityMainThreadDispatcher.Exists())
                            UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.LogError("[VoiceChatUDP] Mensagem muito grande, abortando envio. Reduza duração ou sampleRate."));
                        return;
                    }

                    ushort totalPackets = (ushort)totalPacketsInt;

                    for (int i = 0; i < totalPacketsInt; i++)
                    {
                        int offset = i * payloadPerPacket;
                        int size = Math.Min(payloadPerPacket, allBytes.Length - offset);
                        byte[] packet = new byte[8 + size];

                        // messageId (uint) little-endian
                        packet[0] = (byte)(messageId & 0xFF);
                        packet[1] = (byte)((messageId >> 8) & 0xFF);
                        packet[2] = (byte)((messageId >> 16) & 0xFF);
                        packet[3] = (byte)((messageId >> 24) & 0xFF);

                        // totalPackets (ushort) little-endian
                        packet[4] = (byte)(totalPackets & 0xFF);
                        packet[5] = (byte)((totalPackets >> 8) & 0xFF);

                        // packetIndex (ushort) little-endian
                        ushort idx = (ushort)i;
                        packet[6] = (byte)(idx & 0xFF);
                        packet[7] = (byte)((idx >> 8) & 0xFF);

                        // payload
                        Buffer.BlockCopy(allBytes, offset, packet, 8, size);

                        udp.Send(packet, packet.Length, peerIp, port);
                    }
                }

                if (UnityMainThreadDispatcher.Exists())
                    UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.Log($"[VoiceChatUDP] Áudio enviado: {allBytes.Length} bytes em {Math.Ceiling((double)allBytes.Length / maxPayloadSize)} pacotes."));
            }
            catch (Exception ex)
            {
                if (UnityMainThreadDispatcher.Exists())
                    UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.LogError("[VoiceChatUDP] Erro ao enviar áudio: " + ex.Message));
            }
        });
    }

    // Reassemblagem de pacotes
    void HandleReceivedPacket(uint messageId, ushort totalPackets, ushort packetIndex, byte[] payload)
    {
        lock (reassemblyLock)
        {
            if (!reassemblies.TryGetValue(messageId, out var entry))
            {
                entry = new ReassemblyEntry(totalPackets);
                reassemblies[messageId] = entry;
            }

            if (!entry.parts.ContainsKey(packetIndex))
            {
                entry.parts[packetIndex] = payload;
                entry.received++;
                entry.lastUpdate = DateTime.UtcNow;
            }

            if (entry.received >= entry.total)
            {
                int totalBytes = 0;
                // usar cast para acessar partes por chave ushort
                for (int i = 0; i < entry.total; i++)
                    totalBytes += entry.parts[(ushort)i].Length;

                byte[] all = new byte[totalBytes];
                int pos = 0;
                for (int i = 0; i < entry.total; i++)
                {
                    byte[] part = entry.parts[(ushort)i];
                    Array.Copy(part, 0, all, pos, part.Length);
                    pos += part.Length;
                }

                reassemblies.Remove(messageId);

                int shortCount = all.Length / sizeof(short);
                short[] pcm16 = new short[shortCount];
                Buffer.BlockCopy(all, 0, pcm16, 0, all.Length);

                float[] samples = new float[shortCount];
                for (int i = 0; i < shortCount; i++)
                    samples[i] = pcm16[i] / (float)short.MaxValue;

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