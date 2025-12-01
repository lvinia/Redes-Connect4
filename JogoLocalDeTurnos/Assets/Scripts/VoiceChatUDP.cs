using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class VoiceChatUDP : MonoBehaviour
{
    public static VoiceChatUDP Instance;

    public string peerIp = "10.57.1.134";
    public int port = 5151;
    public int sampleRate = 16000;

    AudioSource audioSource;
    string micDevice;
    AudioClip recordingClip;
    Thread listenerThread;
    volatile bool isRecording = false;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            audioSource = gameObject.AddComponent<AudioSource>();
            StartListening(); // começa a escutar assim que existe
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

        listenerThread = new Thread(() =>
        {
            try
            {
                using (var udp = new UdpClient(port))
                {
                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, port);
                    while (true)
                    {
                        try
                        {
                            var data = udp.Receive(ref ep);
                            if (data == null || data.Length == 0) continue;

                            // converte bytes de volta para floats
                            int floatCount = data.Length / sizeof(float);
                            float[] samples = new float[floatCount];
                            Buffer.BlockCopy(data, 0, samples, 0, data.Length);

                            // Executa na thread principal para reproduzir
                            if (UnityMainThreadDispatcher.Exists())
                                UnityMainThreadDispatcher.Instance().Enqueue(() => PlayClip(samples));
                        }
                        catch (SocketException) { }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[VoiceChatUDP] Listener error: " + ex.Message);
            }
        });
        listenerThread.IsBackground = true;
        listenerThread.Start();
    }

    // Inicia gravação (chame em PointerDown)
    public void BeginRecording()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[VoiceChatUDP] Nenhum microfone detectado.");
            return;
        }

        if (isRecording) return;

        micDevice = Microphone.devices[0];
        // grava em loop com buffer grande (300s), vamos parar manualmente
        recordingClip = Microphone.Start(micDevice, true, 300, sampleRate);
        isRecording = true;
        Debug.Log("[VoiceChatUDP] Iniciando gravação (push-to-talk).");
    }

    // Para gravação e envia (chame em PointerUp)
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

        float[] samples = new float[pos];
        recordingClip.GetData(samples, 0);

        // converte floats para bytes
        byte[] bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        // envia em thread pool para não travar
        ThreadPool.QueueUserWorkItem((_) =>
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    udp.Send(bytes, bytes.Length, peerIp, port);
                }
                Debug.Log("[VoiceChatUDP] Áudio enviado, bytes: " + bytes.Length);
            }
            catch (Exception ex)
            {
                Debug.LogError("[VoiceChatUDP] Erro ao enviar áudio: " + ex.Message);
            }
        });
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

    private void OnApplicationQuit()
    {
        try
        {
            if (listenerThread != null && listenerThread.IsAlive) listenerThread.Abort();
        }
        catch { }
    }
}