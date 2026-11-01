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

    void Update()
    {
        if (Mouse.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        Vector2 screenPos = Mouse.current.position.ReadValue();
        Vector3 world = Camera.main.ScreenToWorldPoint(
            new Vector3(screenPos.x, screenPos.y, 0));
        Vector2Int cell = new Vector2Int(
            Mathf.FloorToInt(world.x), Mathf.FloorToInt(world.y));

        // 移動先のマスをクリックしたら移動
        if (selected != null && movable.Contains(cell))
        {
            selected.cell = cell;
            selected.SnapToCell();
            Debug.Log($"{selected.displayName} が {cell} へ移動");
            Select(null);
            return;
        }

        Unit hit = null;
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u.cell == cell) { hit = u; break; }
        }

        Select(hit);
        if (hit != null)
        {
            ShowMovable(hit);
            Debug.Log($"{hit.displayName}（チーム{hit.team}）を選択。移動 {hit.MoveTiles} マス");
        }
        else
        {
            Debug.Log("選択解除");
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
        }
    }

    void ShowMovable(Unit unit)
    {
        movable.Clear();
        int r = unit.MoveTiles;
        Unit[] all = FindObjectsByType<Unit>();

        for (int dx = -r; dx <= r; dx++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector2Int c = unit.cell + new Vector2Int(dx, dy);
                if (c.x < 0 || c.x > 17 || c.y < 0 || c.y > 17) continue;

                bool occupied = false;
                foreach (Unit u in all)
                {
                    if (u != unit && u.cell == c) { occupied = true; break; }
                }
                if (occupied) continue;

                movable.Add(c);
                GameObject h = Instantiate(highlightPrefab);
                h.transform.position = new Vector3(c.x + 0.5f, c.y + 0.5f, 0);
                highlights.Add(h);
            }
        }
    }

    void ClearHighlights()
    {
        foreach (GameObject h in highlights) Destroy(h);
        highlights.Clear();
        movable.Clear();
    }
}
