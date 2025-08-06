using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum PlayerType { NONE, RED, GREEN }

public struct GridPos { public int row, col; }

public class Board
{
    PlayerType[][] playerBoard;
    GridPos currentPos;

    public Board()
    {
        playerBoard = new PlayerType[6][];
        for (int i = 0; i < playerBoard.Length; i++)
        {
            playerBoard[i] = new PlayerType[7];
            for (int j = 0; j < playerBoard[i].Length; j++)
            {
                playerBoard[i][j] = PlayerType.NONE;
            }
        }
    }

    public void UpdateBoard(int col, bool isPlayer)
    {
        int updatePos = -1;
        for (int i = 5; i >= 0; i--)
        {
            if (playerBoard[i][col] == PlayerType.NONE)
            {
                updatePos = i;
                break;
            }
        }

        if (updatePos != -1)
        {
            playerBoard[updatePos][col] = isPlayer ? PlayerType.RED : PlayerType.GREEN;
            currentPos = new GridPos { row = updatePos, col = col };
        }
        else
        {
            Debug.LogError("Coluna cheia! Tentou jogar em coluna: " + col);
        }
    }

    public bool Result()
    {
        PlayerType current = playerBoard[currentPos.row][currentPos.col];
        if (current == PlayerType.NONE) return false;

        Debug.Log($"[CHECK] Verificando vitória para {current} em linha {currentPos.row}, coluna {currentPos.col}");

        return CheckDirection(current, 0, 1) ||  // Horizontal
               CheckDirection(current, 1, 0) ||  // Vertical
               CheckDirection(current, 1, 1) ||  // Diagonal ↘
               CheckDirection(current, 1, -1);   // Diagonal ↙
    }

    bool CheckDirection(PlayerType current, int dRow, int dCol)
    {
        int count = 1;

        // Verifica em uma direção
        count += CountDirection(current, dRow, dCol);

        // Verifica na direção oposta
        count += CountDirection(current, -dRow, -dCol);

        return count >= 4;
    }

    int CountDirection(PlayerType current, int dRow, int dCol)
    {
        int r = currentPos.row + dRow;
        int c = currentPos.col + dCol;
        int count = 0;

        while (r >= 0 && r < 6 && c >= 0 && c < 7 && playerBoard[r][c] == current)
        {
            count++;
            r += dRow;
            c += dCol;
        }

        return count;
    }

    bool SearchResult(List<GridPos> searchList, PlayerType current)
    {
        int counter = 0;

        for (int i = 0; i < searchList.Count; i++)
        {
            PlayerType compare = playerBoard[searchList[i].row][searchList[i].col];
            if (compare == current)
            {
                counter++;
                if (counter == 4)
                    break;
            }
            else
            {
                counter = 0;
            }
        }

        return counter >= 4;
    }
}