using System;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

public class GameManager : MonoBehaviour
{
    [SerializeField] GameObject red, green;
    bool isPlayer, hasGameFinished, playerIsRed;
    [SerializeField] Text turnMessage;

    const string RED_MESSAGE = "Vez do Vermelho";
    const string GREEN_MESSAGE = "Vez do Verde";

    Color RED_COLOR = new Color(231, 29, 54, 255) / 255f;
    Color GREEN_COLOR = new Color(0, 222, 1, 255) / 255f;

    Board myBoard;

    // TCP P2P
    TcpListener server; 
    Thread listenThread;
    int listenPort = 5050;
    string otherIp = "10.57.1.134"; // coloque o IP real

    private void Awake()
    {
        playerIsRed = PlayerPreferences.Instance.IsPlayerRed;
        isPlayer = playerIsRed;
        hasGameFinished = false;
        turnMessage.text = RED_MESSAGE;
        turnMessage.color = RED_COLOR;
        myBoard = new Board();
        StartServer();

        // Garantir que exista uma instância VoiceChatUDP
        if (VoiceChatUDP.Instance == null)
        {
            var go = new GameObject("VoiceChatUDP");
            var v = go.AddComponent<VoiceChatUDP>();
            v.peerIp = otherIp;
            v.port = 5151;
        }
        else
        {
            VoiceChatUDP.Instance.peerIp = otherIp;
            VoiceChatUDP.Instance.port = 5151;
        }
    }

    void StartServer()
    {
        listenThread = new Thread(() =>
        {
            try
            {
                server = new TcpListener(IPAddress.Any, listenPort);
                server.Start();
                Debug.Log("[P2P] Servidor iniciado na porta " + listenPort);

                while (true)
                {
                    TcpClient client = server.AcceptTcpClient();
                    NetworkStream stream = client.GetStream();

                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Debug.Log("[P2P] Mensagem recebida: " + msg);

                    if (int.TryParse(msg, out int coluna))
                    {
                        UnityMainThreadDispatcher.Instance().Enqueue(() => JogadaRecebida(coluna));
                    }

                    stream.Close();
                    client.Close();
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[P2P] Erro no servidor: " + e.Message);
            }
        });
        listenThread.IsBackground = true;
        listenThread.Start();
    }

    void SendMove(int coluna)
    {
        try
        {
            TcpClient client = new TcpClient();
            client.Connect(otherIp, listenPort);

            NetworkStream stream = client.GetStream();
            byte[] message = Encoding.UTF8.GetBytes(coluna.ToString());
            stream.Write(message, 0, message.Length);

            stream.Close();
            client.Close();

            Debug.Log("[P2P] Jogada enviada: " + coluna);
        }
        catch (Exception e)
        {
            Debug.LogError("[P2P] Erro ao enviar jogada: " + e.Message);
        }
    }

    private void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            if (hasGameFinished || !isPlayer) return;

            Vector3 mousePos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            Vector2 mousePos2D = new Vector2(mousePos.x, mousePos.y);
            RaycastHit2D hit = Physics2D.Raycast(mousePos2D, Vector2.zero);
            if (!hit.collider || !hit.collider.CompareTag("Press")) return;

            Column column = hit.collider.GetComponent<Column>();
            if (column.targetlocation.y > 1.5f) return;

            int coluna = column.col - 1;

            Vector3 spawnPos = column.spawnLocation;
            Vector3 targetPos = column.targetlocation;

            GameObject circle = Instantiate(playerIsRed ? red : green);
            circle.transform.position = spawnPos;
            circle.GetComponent<Mover>().targetPostion = targetPos;

            column.targetlocation += new Vector3(0, 0.7f, 0);

            myBoard.UpdateBoard(coluna, playerIsRed);
            SendMove(coluna);

            if (myBoard.Result())
            {
                turnMessage.text = (playerIsRed ? "Vermelho" : "Verde") + " Ganhou!";
                hasGameFinished = true;
                return;
            }

            isPlayer = false;
            turnMessage.text = playerIsRed ? GREEN_MESSAGE : RED_MESSAGE;
            turnMessage.color = playerIsRed ? GREEN_COLOR : RED_COLOR;
        }
    }

    void JogadaRecebida(int coluna)
    {
        if (hasGameFinished) return;

        Column[] columns = FindObjectsOfType<Column>();
        Column colObj = null;
        foreach (var c in columns)
        {
            if (c.col - 1 == coluna)
            {
                colObj = c;
                break;
            }
        }
        if (colObj == null) return;

        Vector3 spawnPos = colObj.spawnLocation;
        Vector3 targetPos = colObj.targetlocation;

        GameObject circle = Instantiate(playerIsRed ? green : red);
        circle.transform.position = spawnPos;
        circle.GetComponent<Mover>().targetPostion = targetPos;

        colObj.targetlocation += new Vector3(0, 0.7f, 0);

        bool jogadaFoiDoOponente = !playerIsRed;
        myBoard.UpdateBoard(coluna, jogadaFoiDoOponente);

        if (myBoard.Result())
        {
            string vencedor = playerIsRed ? "Verde" : "Vermelho";
            turnMessage.text = vencedor + " Ganhou!";
            hasGameFinished = true;
            return;
        }

        isPlayer = true;
        turnMessage.text = playerIsRed ? RED_MESSAGE : GREEN_MESSAGE;
        turnMessage.color = playerIsRed ? RED_COLOR : GREEN_COLOR;
    }

    private void OnApplicationQuit()
    {
        try
        {
            server?.Stop();
            listenThread?.Abort();
        }
        catch { }
    }
}