using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class P2PNetwork
{
    private TcpListener server;
    private int listenPort;
    private Thread listenThread;
    private CancellationTokenSource listenCts;

    public Action<int> OnMoveReceived;

    public P2PNetwork(int port)
    {
        listenPort = port;
        StartServer();
    }

    void StartServer()
    {
        listenCts = new CancellationTokenSource();
        listenThread = new Thread(() => {
            try
            {
                server = new TcpListener(IPAddress.Any, listenPort);
                server.Start();
                Debug.Log("[P2P] Servidor ouvindo na porta " + listenPort);

                while (!listenCts.Token.IsCancellationRequested)
                {
                    TcpClient client = server.AcceptTcpClient();
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] buffer = new byte[1024];
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            Debug.Log("[P2P] Jogada recebida: " + msg);

                            if (int.TryParse(msg, out int col))
                            {
                                OnMoveReceived?.Invoke(col);
                            }
                        }
                    }
                    client.Close();
                }
            }
            catch (SocketException se)
            {
                Debug.Log("[P2P] Listener finalizado: " + se.Message);
            }
            catch (Exception e)
            {
                Debug.LogError("[P2P] Erro no listener: " + e.Message);
            }
            finally
            {
                server = null;
            }
        }) { IsBackground = true };
        listenThread.Start();
    }

    public void SendMove(string ip, int port, int column)
    {
        try
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(ip, port);

                using (NetworkStream stream = client.GetStream())
                {
                    byte[] message = Encoding.UTF8.GetBytes(column.ToString());
                    stream.Write(message, 0, message.Length);
                }
            }

            Debug.Log("[P2P] Jogada enviada: " + column);
        }
        catch (Exception e)
        {
            Debug.LogError("[P2P] Erro ao enviar jogada: " + e.Message);
        }
    }

    public void StopServer()
    {
        try
        {
            listenCts?.Cancel();
            server?.Stop();
            if (listenThread != null && listenThread.IsAlive)
                listenThread.Join(500);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Erro ao parar servidor: " + ex.Message);
        }
    }
}


