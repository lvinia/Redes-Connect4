using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

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
    class ReassemblyEntry
    {
        public int TotalPackets;
        public DateTime FirstSeen;
        public Dictionary<int, byte[]> Fragments = new Dictionary<int, byte[]>();
    }

    readonly Dictionary<uint, ReassemblyEntry> reassemblies = new Dictionary<uint, ReassemblyEntry>();
    readonly object reassemblyLock = new object();

    // Cleanup
    const int REASSEMBLY_TIMEOUT_SECONDS = 10;

    // Cancellation
    CancellationTokenSource listenerCts;

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

    public void StartListening()
    {
        if (listenerThread != null && listenerThread.IsAlive) return;

        listenerCts = new CancellationTokenSource();
        listenerThread = new Thread(() => {
            try
            {
                using (var udp = new UdpClient(port))
                {
                    IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    while (!listenerCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var data = udp.Receive(ref remoteEp);
                            if (data == null || data.Length < 8) continue; // header mínimo

                            // Parse header: [uint messageId (4)] [ushort totalPackets (2)] [ushort packetIndex (2)]
                            uint messageId = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                            ushort totalPackets = (ushort)(data[4] | (data[5] << 8));
                            ushort packetIndex = (ushort)(data[6] | (data[7] << 8));

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
            catch (SocketException se)
            {
                Debug.Log("[VoiceChatUDP] Listener stopped: " + se.Message);
            }
            catch (Exception ex)
            {
                Debug.LogError("[VoiceChatUDP] Listener fatal: " + ex.Message);
            }
        })
        { IsBackground = true };
        listenerThread.Start();
    }

    public void StopListening()
    {
        try
        {
            listenerCts?.Cancel();
            // fechar sockets será feito pelo using quando o thread terminar
            if (listenerThread != null && listenerThread.IsAlive)
            {
                listenerThread.Join(300);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[VoiceChatUDP] Erro ao parar listener: " + ex.Message);
        }
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

        // Fragmentar e enviar em thread pool
        ThreadPool.QueueUserWorkItem((_) =>
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    // message id único (uint)
                    uint messageId = (uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue);
                    int payloadPerPacket = maxPayloadSize;
                    int totalPacketsInt = (allBytes.Length + payloadPerPacket - 1) / payloadPerPacket;

                    if (totalPacketsInt > ushort.MaxValue)
                    {
                        Debug.LogError("[VoiceChatUDP] Mensagem muito grande, abortando envio. Reduza duração ou sampleRate.");
                        return;
                    }

                    ushort totalPackets = (ushort)totalPacketsInt;

                    for (int i = 0; i < totalPacketsInt; i++)
                    {
                        ushort packetIndex = (ushort)i;

                        int offset = i * payloadPerPacket;
                        int size = Math.Min(payloadPerPacket, allBytes.Length - offset);

                        // header: 4 + 2 + 2 = 8 bytes
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
                        packet[6] = (byte)(packetIndex & 0xFF);
                        packet[7] = (byte)((packetIndex >> 8) & 0xFF);

                        Buffer.BlockCopy(allBytes, offset, packet, 8, size);

                        udp.Send(packet, packet.Length, peerIp, port);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[VoiceChatUDP] Erro ao enviar áudio: " + ex.Message);
            }
        });
    }

    void HandleReceivedPacket(uint messageId, ushort totalPackets, ushort packetIndex, byte[] payload)
    {
        lock (reassemblyLock)
        {
            if (!reassemblies.TryGetValue(messageId, out var entry))
            {
                entry = new ReassemblyEntry { TotalPackets = totalPackets, FirstSeen = DateTime.UtcNow };
                reassemblies[messageId] = entry;
            }

            // store fragment
            entry.Fragments[(int)packetIndex] = payload;

            // check if complete
            if (entry.Fragments.Count == entry.TotalPackets)
            {
                // reconstruct
                int totalLen = 0;
                for (int i = 0; i < entry.TotalPackets; i++)
                {
                    totalLen += entry.Fragments[i].Length;
                }

                byte[] all = new byte[totalLen];
                int ptr = 0;
                for (int i = 0; i < entry.TotalPackets; i++)
                {
                    var frag = entry.Fragments[i];
                    Buffer.BlockCopy(frag, 0, all, ptr, frag.Length);
                    ptr += frag.Length;
                }

                // converter bytes para short[] e tocar
                short[] pcm16 = new short[all.Length / 2];
                Buffer.BlockCopy(all, 0, pcm16, 0, all.Length);

                float[] samples = new float[pcm16.Length];
                for (int i = 0; i < pcm16.Length; i++)
                    samples[i] = pcm16[i] / (float)short.MaxValue;

                // criar clip e tocar
                AudioClip clip = AudioClip.Create("vc_incoming", samples.Length, 1, sampleRate, false);
                clip.SetData(samples, 0);
                UnityMainThreadDispatcher.Enqueue(() => { audioSource.PlayOneShot(clip); });

                // remover reassembly
                reassemblies.Remove(messageId);
            }
        }
    }

    void StartCleanupThread()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                Thread.Sleep(1000);
                lock (reassemblyLock)
                {
                    var keys = new List<uint>(reassemblies.Keys);
                    foreach (var k in keys)
                    {
                        if ((DateTime.UtcNow - reassemblies[k].FirstSeen).TotalSeconds > REASSEMBLY_TIMEOUT_SECONDS)
                        {
                            reassemblies.Remove(k);
                        }
                    }
                }
            }
        });
    }
}
