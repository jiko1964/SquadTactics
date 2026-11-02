using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ゲームHUD（レイアウト案A：左右パネル型）
// ・左パネル＝チーム1、右パネル＝チーム2のユニットカード（名前／HP／EX／状態／指示済み）
// ・カードをクリックすると、そのユニットを盤面クリックと同じ扱いで選択する
// ・下部に選択中ユニットの詳細と、ゲームログ（Debug.Log）を表示する
public class HudController : MonoBehaviour
{
    [Header("ユニットカード：チーム1（左パネル）")]
    public Button[] team1Cards = new Button[3];
    [Header("ユニットカード：チーム2（右パネル）")]
    public Button[] team2Cards = new Button[3];
    [Header("テキスト")]
    public TextMeshProUGUI detailText;
    public TextMeshProUGUI logText;
    [Header("参照（空欄なら自動で探す）")]
    public GameController game;

    private const int MaxLogLines = 7;
    private readonly List<string> logLines = new();
    private readonly Dictionary<Button, Color> baseColors = new();
    private Unit[] team1Units = new Unit[0];
    private Unit[] team2Units = new Unit[0];
    private bool wired = false;
    private float timer = 0f;

    void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    // ゲームのログ（Debug.Log のみ。警告・エラーは載せない）を画面内ログへ
    void HandleLog(string logString, string stackTrace, LogType type)
    {
        if (type != LogType.Log) return;
        logLines.Add(logString);
        while (logLines.Count > MaxLogLines) logLines.RemoveAt(0);
        if (logText != null) logText.text = string.Join("\n", logLines);
    }

    void Update()
    {
        if (game == null) game = FindAnyObjectByType<GameController>();
        if (!wired) TryWire();
        if (!wired) return;

        timer -= Time.deltaTime;
        if (timer > 0f) return;
        timer = 0.2f;
        Refresh();
    }

    // 出現したユニットを両チーム分そろえて、カードと結びつける（初回だけ）
    void TryWire()
    {
        Unit[] all = FindObjectsByType<Unit>();
        if (all.Length < 6) return; // スポーン前なら待つ

        team1Units = all.Where(u => u.team == 1).OrderBy(u => u.cell.x).ToArray();
        team2Units = all.Where(u => u.team == 2).OrderBy(u => u.cell.x).ToArray();
        if (team1Units.Length != 3 || team2Units.Length != 3) return;

        WireCards(team1Cards, team1Units);
        WireCards(team2Cards, team2Units);
        wired = true;
        Refresh();
    }

    void WireCards(Button[] cards, Unit[] units)
    {
        for (int i = 0; i < cards.Length && i < units.Length; i++)
        {
            Button b = cards[i];
            if (b == null) continue;
            if (b.image != null) baseColors[b] = b.image.color;
            Unit u = units[i];
            b.onClick.AddListener(() => { if (game != null) game.HudSelect(u); });
        }
    }

    void Refresh()
    {
        RefreshCards(team1Cards, team1Units);
        RefreshCards(team2Cards, team2Units);
        RefreshDetail();
    }

    void RefreshCards(Button[] cards, Unit[] units)
    {
        Unit sel = game != null ? game.SelectedUnit : null;
        for (int i = 0; i < cards.Length && i < units.Length; i++)
        {
            Button b = cards[i];
            if (b == null) continue;
            Unit u = units[i];
            bool alive = u.gameObject.activeSelf;
            b.interactable = alive;

            TextMeshProUGUI label = b.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = CardText(u, sel == u, alive);

            if (b.image != null && baseColors.TryGetValue(b, out Color baseColor))
            {
                if (!alive) b.image.color = baseColor * 0.4f;
                else if (sel == u) b.image.color = Color.Lerp(baseColor, Color.white, 0.35f);
                else b.image.color = baseColor;
            }
        }
    }

    string CardText(Unit u, bool isSelected, bool alive)
    {
        string head = (isSelected ? "▶ " : "") + u.displayName;
        if (!alive) return head + "\n撃破";
        string line2 = $"HP {u.hp}/{u.maxHp}　EX {u.exPoints}/{Unit.MaxEx}";
        var tags = new List<string>();
        if (game != null && game.OrderTextFor(u) != "") tags.Add("指示済");
        if (u.poisoned) tags.Add("毒");
        if (u.beaconed) tags.Add("ビーコン");
        if (u.stunTurns > 0) tags.Add("スタン");
        if (u.camouflaged) tags.Add("迷彩");
        string line3 = string.Join("・", tags);
        return line3 == "" ? head + "\n" + line2 : head + "\n" + line2 + "\n" + line3;
    }

    void RefreshDetail()
    {
        if (detailText == null) return;
        Unit u = game != null ? game.SelectedUnit : null;
        if (u == null)
        {
            detailText.text = "ユニット未選択\n（盤面かカードをクリック）";
            return;
        }

        string weapon = u.weapon != null
            ? $"射程{u.weaponRange} 威力{u.weaponPower} 命中{u.weaponAccuracy}"
            : "なし";
        string ult = u.data != null ? u.data.ultName : "―";
        string equip = "なし";
        if (u.data != null && u.data.equipment != null)
        {
            var names = u.data.equipment.Where(e => e != null).Select(e => e.itemName).ToArray();
            if (names.Length > 0) equip = string.Join("・", names);
        }
        var states = new List<string>();
        if (u.poisoned) states.Add("毒");
        if (u.beaconed) states.Add("ビーコン");
        if (u.stunTurns > 0) states.Add("スタン");
        if (u.camouflaged) states.Add("光学迷彩");
        if (u.defending) states.Add("防御中");
        if (u.ultGuarding) states.Add("鉄壁");
        string state = states.Count > 0 ? string.Join("・", states) : "なし";
        string order = game != null ? game.OrderTextFor(u) : "";

        detailText.text =
            $"{u.displayName}（チーム{u.team}）　HP {u.hp}/{u.maxHp}　EX {u.exPoints}/{Unit.MaxEx}\n" +
            $"攻撃{u.attack} 防御{u.defense} 精密{u.precision} 敏捷{u.agility} 機動力{u.mobility}（移動{u.MoveTiles}）索敵{u.search}\n" +
            $"武器：{weapon}　ULT：{ult}\n" +
            $"装備：{equip}\n" +
            $"状態：{state}" +
            (order != "" ? $"\n指示：{order}" : "");
    }
}
