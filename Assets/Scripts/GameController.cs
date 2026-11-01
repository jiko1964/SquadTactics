using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class GameController : MonoBehaviour
{
    public GameObject highlightPrefab;

    private Unit selected;
    private Color originalColor;
    private Vector3 originalScale;
    private readonly List<GameObject> highlights = new();
    private readonly HashSet<Vector2Int> movable = new();
    private readonly HashSet<Vector2Int> attackable = new();

    void Update()
    {
        if (Mouse.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        Vector2 screenPos = Mouse.current.position.ReadValue();
        Vector3 world = Camera.main.ScreenToWorldPoint(
            new Vector3(screenPos.x, screenPos.y, 0));
        Vector2Int cell = new Vector2Int(
            Mathf.FloorToInt(world.x), Mathf.FloorToInt(world.y));

        Unit clicked = FindUnitAt(cell);

        // 射程内の敵をクリック → 攻撃
        if (selected != null && clicked != null
            && clicked.team != selected.team && attackable.Contains(cell))
        {
            Attack(selected, clicked);
            Select(null);
            return;
        }

        // 移動先をクリック → 移動（選択は維持され、そのまま攻撃もできる）
        if (selected != null && movable.Contains(cell))
        {
            selected.cell = cell;
            selected.SnapToCell();
            Debug.Log($"{selected.displayName} が {cell} へ移動");
            Select(selected);
            return;
        }

        Select(clicked);
        if (clicked != null)
            Debug.Log($"{clicked.displayName}（チーム{clicked.team}）を選択。HP {clicked.hp}、射程 {clicked.weaponRange}");
        else
            Debug.Log("選択解除");
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
        // ステップ4の仮ダメージ（武器威力そのまま）。ステップ5で計算式に差し替え
        int damage = attacker.weaponPower;
        target.hp -= damage;
        Debug.Log($"{attacker.displayName} の攻撃！ {target.displayName} に {damage} ダメージ（残りHP {Mathf.Max(target.hp, 0)}）");
        if (target.hp <= 0)
        {
            Debug.Log($"{target.displayName} を撃破！");
            Destroy(target.gameObject);
        }
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

        // 移動範囲（8方向・四角）
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

        // 攻撃範囲（ルール通り円形＝直線距離で判定）
        for (int dx = -unit.weaponRange; dx <= unit.weaponRange; dx++)
        {
            for (int dy = -unit.weaponRange; dy <= unit.weaponRange; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector2Int c = unit.cell + new Vector2Int(dx, dy);
                if (!Inside(c)) continue;
                if (Vector2.Distance(unit.cell, c) > unit.weaponRange) continue;

                attackable.Add(c);
                AddHighlight(c, new Color(1f, 0f, 0f, 0.35f));
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
