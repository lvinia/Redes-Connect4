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
    int sampleRate = 8000;
    AudioSource audioSource;
    string micDevice;
    Thread listenerThread;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        else
            Destroy(gameObject);
    }

    public void StartListening()
    {
        if (listenerThread != null && listenerThread.IsAlive)
            return; // já está escutando

        listenerThread = new Thread(() => {
            using (var udp = new UdpClient(port))
            {
                IPEndPoint ep = null;
                while (true)
                {
                    try
                    {
                        var data = udp.Receive(ref ep);
                        float[] samples = new float[data.Length / sizeof(float)];
                        Buffer.BlockCopy(data, 0, samples, 0, data.Length);
                        UnityMainThreadDispatcher.Instance().Enqueue(() => PlayClip(samples));
                    }
                    catch { }
                }
            }
        });
        listenerThread.IsBackground = true;
        listenerThread.Start();
    }

    public void StartRecordingAndSend(int recordSeconds = 2)
    {
        if (Microphone.devices.Length == 0) return;
        micDevice = Microphone.devices[0];
        Microphone.End(micDevice);
        AudioClip clip = Microphone.Start(micDevice, false, recordSeconds, sampleRate);
        Instance.StartCoroutine(RecordAndSendRoutine(clip, recordSeconds));
    }

    System.Collections.IEnumerator RecordAndSendRoutine(AudioClip clip, int recordSeconds)
    {
        yield return new WaitForSeconds(recordSeconds);
        Microphone.End(micDevice);
        float[] samples = new float[clip.samples];
        clip.GetData(samples, 0);

        byte[] bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        ThreadPool.QueueUserWorkItem((_) =>
        {
            using (var udp = new UdpClient())
                udp.Send(bytes, bytes.Length, peerIp, port);
        });
    }

    void PlayClip(float[] samples)
    {
        AudioClip clip = AudioClip.Create("UDPVoice", samples.Length, 1, sampleRate, false);
        clip.SetData(samples, 0);
        audioSource.Stop();
        audioSource.clip = clip;
        audioSource.Play();
    }

    private void OnApplicationQuit()
    {
        if (listenerThread != null && listenerThread.IsAlive)
            listenerThread.Abort();
    }
}