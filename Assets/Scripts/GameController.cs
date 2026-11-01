using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

public class GameController : MonoBehaviour
{
    public GameObject highlightPrefab;
    public TextMeshProUGUI turnText;

    private Unit selected;
    private Color originalColor;
    private Vector3 originalScale;
    private readonly List<GameObject> highlights = new();
    private readonly HashSet<Vector2Int> movable = new();
    private readonly HashSet<Vector2Int> attackable = new();

    public int currentTeam = 1;
    public int turnNumber = 1;
    private bool gameOver = false;
    private bool attackPhase = false; // false=移動フェーズ、true=攻撃フェーズ

    void Start()
    {
        UpdateTurnText();
    }

    void Update()
    {
        if (gameOver) return;

        if (Keyboard.current != null
        && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            EndTurn();
            return;
        }

        if (Mouse.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        Vector2 screenPos = Mouse.current.position.ReadValue();
        Vector3 world = Camera.main.ScreenToWorldPoint(
        new Vector3(screenPos.x, screenPos.y, 0));
        Vector2Int cell = new Vector2Int(
        Mathf.FloorToInt(world.x), Mathf.FloorToInt(world.y));

        Unit clicked = FindUnitAt(cell);

        // 射程内の敵をクリック → 対象指定 → 成否判定
        if (selected != null && attackPhase && clicked != null
        && clicked.team != selected.team && attackable.Contains(cell))
        {
            if (!selected.hasAttacked) Attack(selected, clicked);
            else Debug.Log($"{selected.displayName} は攻撃済みです");
            Select(null);
            return;
        }

        // 移動先をクリック → 移動確定 → 攻撃フェーズへ
        if (selected != null && !attackPhase && movable.Contains(cell))
        {
            selected.cell = cell;
            selected.SnapToCell();
            selected.movedThisTurn = true;
            Debug.Log($"{selected.displayName} が {cell} へ移動。次に攻撃対象を指定");
            attackPhase = true;
            Select(selected);
            return;
        }

        // 自分自身をクリック → 移動しないで攻撃フェーズへ
        if (selected != null && !attackPhase && clicked == selected)
        {
            Debug.Log($"{selected.displayName} は移動しない（静止ボーナス）。攻撃対象を指定");
            attackPhase = true;
            Select(selected);
            return;
        }

        // 空マスをクリック → 攻撃しないで行動終了
        if (selected != null && attackPhase && clicked == null)
        {
            Debug.Log($"{selected.displayName} は攻撃しないで行動終了");
            selected.hasAttacked = true;
            Select(null);
            return;
        }

        // 選択（自チームのみ。移動済みのユニットは攻撃フェーズから始まる）
        if (clicked != null && clicked.team == currentTeam)
        {
            attackPhase = clicked.movedThisTurn;
            Select(clicked);
            Debug.Log($"{clicked.displayName}（チーム{clicked.team}）を選択。HP {clicked.hp}");
        }
        else
        {
            Select(null);
            if (clicked == null) Debug.Log("選択解除");
        }
    }

    public void EndTurn()
    {
        if (gameOver) return;
        Select(null);
        attackPhase = false;
        currentTeam = 3 - currentTeam;
        if (currentTeam == 1) turnNumber++;

        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u.team == currentTeam)
            {
                u.movedThisTurn = false;
                u.hasAttacked = false;
            }
        }
        Debug.Log($"―― ターン{turnNumber}：チーム{currentTeam} の番 ――");
        UpdateTurnText();
    }

    void CheckWin()
    {
        int team1 = 0, team2 = 0;
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u.team == 1) team1++; else team2++;
        }
        if (team1 == 0 || team2 == 0)
        {
            gameOver = true;
            int winner = team1 > 0 ? 1 : 2;
            Debug.Log($"チーム{winner} の勝利！");
            if (turnText != null) turnText.text = $"Team {winner} Wins!";
        }
    }

    void UpdateTurnText()
    {
        if (turnText != null)
            turnText.text = $"Turn {turnNumber} / Team {currentTeam}";
    }

    Unit FindUnitAt(Vector2Int cell)
    {
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u.cell == cell) return u;
        }
        return null;
    }

    void Attack(Unit attacker, Unit target)
    {
        int hitRate = Mathf.Clamp(
        Mathf.RoundToInt(attacker.weaponAccuracy
        + (attacker.precision - target.agility) / 2f
        + (attacker.movedThisTurn ? 0 : 10)),
        5, 95);

        int roll = Random.Range(1, 101);
        if (roll > hitRate)
        {
            Debug.Log($"{attacker.displayName} の攻撃！ 命中率 {hitRate}% → 出目 {roll} で 外れ");
        }
        else
        {
            float raw = (attacker.attack + attacker.weaponPower) / 2f
            * 100f / (100 + target.defense);
            if (!attacker.movedThisTurn) raw *= 1.1f;
            int damage = Mathf.Max(1, Mathf.RoundToInt(raw));

            target.hp -= damage;
            Debug.Log($"{attacker.displayName} の攻撃！ 命中率 {hitRate}% → 出目 {roll} で 命中。{target.displayName} に {damage} ダメージ（残りHP {Mathf.Max(target.hp, 0)}）");
            if (target.hp <= 0)
            {
                Debug.Log($"{target.displayName} を撃破！");
                target.gameObject.SetActive(false); // 即座に非表示＆集計対象外
            }
        }
        attacker.hasAttacked = true;
        CheckWin();
    }

    void Select(Unit u)
    {
        ClearHighlights();
        if (selected != null)
        {
            selected.GetComponent<SpriteRenderer>().color = originalColor;
            selected.transform.localScale = originalScale;
        }
        selected = u;
        if (selected != null)
        {
            var sr = selected.GetComponent<SpriteRenderer>();
            originalColor = sr.color;
            originalScale = selected.transform.localScale;
            sr.color = Color.yellow;
            selected.transform.localScale = originalScale * 1.2f;
            ShowRanges(selected);
        }
    }

    void ShowRanges(Unit unit)
    {
        movable.Clear();
        attackable.Clear();
        Unit[] all = FindObjectsByType<Unit>();

        // 移動フェーズのときだけ移動範囲を表示
        if (!attackPhase && !unit.movedThisTurn)
        {
            int r = unit.MoveTiles;
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    Vector2Int c = unit.cell + new Vector2Int(dx, dy);
                    if (!Inside(c)) continue;

                    bool occupied = false;
                    foreach (Unit u in all)
                    {
                        if (u != unit && u.cell == c) { occupied = true; break; }
                    }
                    if (occupied) continue;

                    movable.Add(c);
                    AddHighlight(c, null);
                }
            }
        }

        // 攻撃範囲は常時表示（移動フェーズでは薄く、攻撃フェーズでは濃く）
        if (!unit.hasAttacked)
        {
            Color atkColor = attackPhase
            ? new Color(1f, 0f, 0f, 0.35f) // 攻撃フェーズ：濃い赤
            : new Color(1f, 0f, 0f, 0.15f); // 移動フェーズ：薄い赤

            for (int dx = -unit.weaponRange; dx <= unit.weaponRange; dx++)
            {
                for (int dy = -unit.weaponRange; dy <= unit.weaponRange; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    Vector2Int c = unit.cell + new Vector2Int(dx, dy);
                    if (!Inside(c)) continue;
                    if (Vector2.Distance(unit.cell, c) > unit.weaponRange) continue;

                    attackable.Add(c);
                    AddHighlight(c, atkColor);
                }
            }
        }
    }

    bool Inside(Vector2Int c) => c.x >= 0 && c.x <= 17 && c.y >= 0 && c.y <= 17;

    void AddHighlight(Vector2Int c, Color? color)
    {
        GameObject h = Instantiate(highlightPrefab);
        h.transform.position = new Vector3(c.x + 0.5f, c.y + 0.5f, 0);
        if (color.HasValue)
            h.GetComponent<SpriteRenderer>().color = color.Value;
        highlights.Add(h);
    }

    void ClearHighlights()
    {
        foreach (GameObject h in highlights) Destroy(h);
        highlights.Clear();
        movable.Clear();
        attackable.Clear();
    }
}
