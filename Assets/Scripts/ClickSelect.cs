using UnityEngine;
using UnityEngine.InputSystem;

public class ClickSelect : MonoBehaviour
{
    private Unit selected;
    private Color originalColor;
    private Vector3 originalScale;

    void Update()
    {
        if (Mouse.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        Vector2 screenPos = Mouse.current.position.ReadValue();
        Vector3 world = Camera.main.ScreenToWorldPoint(
            new Vector3(screenPos.x, screenPos.y, 0));
        Vector2Int cell = new Vector2Int(
            Mathf.FloorToInt(world.x), Mathf.FloorToInt(world.y));

        Unit hit = null;
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u.cell == cell) { hit = u; break; }
        }

        Select(hit);
        if (hit != null)
            Debug.Log($"{hit.displayName}（チーム{hit.team}）を選択。マス {hit.cell}");
        else
            Debug.Log("選択解除");
    }

    void Select(Unit u)
    {
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
}
