using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 編制画面（フェイズ4・本体）：キャラ3人の選択＋武器／装備の購入（予算制）を
// 実行時にUIごと生成する。両チームの編制が終わると、選んだ編成でユニットを
// スポーンして GameController を有効化し、対戦を開始する。
// ・キャラの標準装備（CharacterData の defaultWeapon / equipment）を初期状態として
// 読み込み、そこから差し替える形にした
// ・スポーン位置と色付けは UnitSpawner の標準編成と同じ仕様
// ・「標準編成で開始」を選ぶと、UnitSpawner と GameController を有効化して
// 従来どおりの固定編成で始める
public class SquadBuilder : MonoBehaviour
{
    [Header("カタログ（Inspectorに全アセットを登録）")]
    public CharacterData[] characters;
    public WeaponData[] weapons;
    public EquipmentData[] equipmentCatalog;

    [Header("参照")]
    public GameObject unitPrefab;
    public GameController game;
    public UnitSpawner spawner;
    public TMP_FontAsset font;

    [Header("設定")]
    public int budget = 1000;

    class Pick
    {
        public CharacterData character;
        public WeaponData weapon;
        public List<EquipmentData> items = new List<EquipmentData>();
    }

    int phaseTeam = 1; // いま編成中のチーム（1→2）
    Pick[] picks = new Pick[3]; // 編成中のチームの選択
    readonly Pick[][] decided = new Pick[2][]; // 確定済み [0]=チーム1 [1]=チーム2
    int activeSlot = 0; // 編集中のスロット

    Canvas canvas;
    TextMeshProUGUI titleLabel, budgetLabel, messageLabel, detailLabel;
    TextMeshProUGUI confirmLabel;
    Button confirmButton;
    readonly List<Button> rosterButtons = new List<Button>();
    readonly List<Button> slotButtons = new List<Button>();
    readonly List<Button> weaponButtons = new List<Button>();
    readonly List<Button> equipButtons = new List<Button>();

    // 効果が未実装の装備（買えるが、現状では効果がないもの）
    static readonly HashSet<string> Unimplemented = new HashSet<string>
{
"射程強化", "炸裂弾", "ロングバレル", "テレポートユニット", "バリケードユニット"
};

    void Start()
    {
        BuildUi();
        Refresh();
    }

    // ---------- UI生成 ----------

    RectTransform Rt(GameObject go, Transform parent, float x, float y, float w, float h)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    TextMeshProUGUI MakeLabel(Transform parent, float x, float y, float w, float h,
    int size, TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
    {
        var go = new GameObject("Label");
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.fontSize = size;
        t.alignment = align;
        t.color = new Color(0.92f, 0.93f, 0.97f);
        t.textWrappingMode = TextWrappingModes.Normal;
        Rt(go, parent, x, y, w, h);
        return t;
    }

    Button MakeButton(Transform parent, float x, float y, float w, float h,
    UnityEngine.Events.UnityAction onClick, int fontSize = 14)
    {
        var go = new GameObject("Button");
        var img = go.AddComponent<Image>();
        img.color = new Color(0.20f, 0.22f, 0.28f, 1f);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var textGo = new GameObject("Text");
        var t = textGo.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.fontSize = fontSize;
        t.alignment = TextAlignmentOptions.Center;
        t.color = new Color(0.92f, 0.93f, 0.97f);
        var trt = textGo.GetComponent<RectTransform>();
        trt.SetParent(go.transform, false);
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(4, 2);
        trt.offsetMax = new Vector2(-4, -2);
        Rt(go, parent, x, y, w, h);
        if (onClick != null) btn.onClick.AddListener(onClick);
        return btn;
    }

    static void SetButtonText(Button b, string text)
    {
        var t = b.GetComponentInChildren<TextMeshProUGUI>();
        if (t != null) t.text = text;
    }

    static void SetButtonColor(Button b, Color c)
    {
        var img = b.GetComponent<Image>();
        if (img != null) img.color = c;
    }

    void BuildUi()
    {
        var canvasGo = new GameObject("SquadBuilderCanvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600, 900);
        canvasGo.AddComponent<GraphicRaycaster>();
        var root = canvasGo.transform;

        var bg = new GameObject("Background");
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0.07f, 0.08f, 0.11f, 0.98f);
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.SetParent(root, false);
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        titleLabel = MakeLabel(root, 30, 18, 700, 40, 26);
        budgetLabel = MakeLabel(root, 1050, 22, 520, 36, 20, TextAlignmentOptions.TopRight);

        MakeLabel(root, 30, 72, 300, 26, 17).text = "キャラクター（3人選択）";
        if (characters != null)
        {
            for (int i = 0; i < characters.Length; i++)
            {
                var c = characters[i];
                var b = MakeButton(root, 30, 104 + i * 60, 310, 52, () => OnCharacterClicked(c));
                rosterButtons.Add(b);
            }
        }

        MakeLabel(root, 370, 72, 300, 26, 17).text = "部隊（クリックで編集中を切替）";
        for (int i = 0; i < 3; i++)
        {
            int slot = i;
            var b = MakeButton(root, 370, 104 + i * 60, 310, 52, () => { activeSlot = slot; Refresh(); });
            slotButtons.Add(b);
        }
        detailLabel = MakeLabel(root, 370, 300, 310, 480, 14);

        MakeLabel(root, 720, 72, 400, 26, 17).text = "武器（編集中の隊員に装備）";
        if (weapons != null)
        {
            for (int i = 0; i < weapons.Length; i++)
            {
                var w = weapons[i];
                int col = i % 2, row = i / 2;
                var b = MakeButton(root, 720 + col * 225, 104 + row * 48, 215, 42, () => OnWeaponClicked(w), 13);
                weaponButtons.Add(b);
            }
        }

        MakeLabel(root, 720, 356, 400, 26, 17).text = "装備（クリックで購入／解除）";
        if (equipmentCatalog != null)
        {
            for (int i = 0; i < equipmentCatalog.Length; i++)
            {
                var item = equipmentCatalog[i];
                int col = i % 2, row = i / 2;
                var b = MakeButton(root, 720 + col * 225, 388 + row * 44, 215, 38, () => OnEquipmentClicked(item), 12);
                equipButtons.Add(b);
            }
        }

        messageLabel = MakeLabel(root, 30, 836, 900, 30, 15);
        var skip = MakeButton(root, 30, 786, 300, 40, StartWithStandardSquads, 13);
        SetButtonText(skip, "標準編成で開始（編成をスキップ）");
        confirmButton = MakeButton(root, 1170, 796, 400, 64, OnConfirm, 19);
        confirmLabel = confirmButton.GetComponentInChildren<TextMeshProUGUI>();
    }

    // ---------- 表示更新 ----------

    Pick ActivePick => (activeSlot >= 0 && activeSlot < 3) ? picks[activeSlot] : null;

    void Refresh()
    {
        titleLabel.text = $"チーム{phaseTeam} 編成";
        budgetLabel.text = $"資金 {budget - TeamCost()} ／ {budget}（使用 {TeamCost()}）";

        // キャラ一覧
        for (int i = 0; i < rosterButtons.Count; i++)
        {
            var c = characters[i];
            int slot = IndexOfCharacter(c);
            SetButtonText(rosterButtons[i],
            $"{c.characterName}　HP{c.maxHp} 攻{c.attack} 防{c.defense}" + (slot >= 0 ? $"　" : ""));
            SetButtonColor(rosterButtons[i], slot >= 0
            ? new Color(0.22f, 0.38f, 0.30f, 1f)
            : new Color(0.20f, 0.22f, 0.28f, 1f));
        }

        // 部隊スロット
        for (int i = 0; i < 3; i++)
        {
            var p = picks[i];
            SetButtonText(slotButtons[i], p == null || p.character == null
            ? $"{i + 1}人目：未選択"
            : $"{i + 1}人目：{p.character.characterName}（{PickCost(p)}）");
            SetButtonColor(slotButtons[i], i == activeSlot
            ? new Color(0.30f, 0.42f, 0.70f, 1f)
            : new Color(0.20f, 0.22f, 0.28f, 1f));
        }

        // 編集中の隊員の詳細
        var ap = ActivePick;
        if (ap == null || ap.character == null)
        {
            detailLabel.text = "隊員未選択\n左の一覧からキャラクターを選んでください。\n\n選んだ隊員には、そのキャラの標準装備が\n最初から付いています。武器と装備は\n右のショップで差し替えられます。";
        }
        else
        {
            var d = ap.character;
            string items = ap.items.Count == 0
            ? "なし"
            : string.Join("\n", ap.items.Select(i => $"・{i.itemName}（{i.price}）"));
            detailLabel.text =
            $"{d.characterName}（{activeSlot + 1}人目）\n" +
            $"HP{d.maxHp} 攻撃{d.attack} 防御{d.defense}\n" +
            $"精密{d.precision} 敏捷{d.agility} 機動力{d.mobility} 索敵{d.search}\n" +
            $"ULT：{d.ultName}\n\n" +
            $"武器：{(ap.weapon != null ? $"{WeaponDisplayName(ap.weapon)}（{WeaponPrice(ap.weapon)}）" : "なし")}\n" +
            $"装備（{ap.items.Count + 1}/6枠）：\n{items}\n\n" +
            $"この隊員の合計：{PickCost(ap)}";
        }

        // 武器ボタン
        for (int i = 0; i < weaponButtons.Count; i++)
        {
            var w = weapons[i];
            bool equipped = ap != null && ap.weapon == w;
            SetButtonText(weaponButtons[i],
            $"{WeaponDisplayName(w)} 威{w.power} 命{w.accuracy} 射{w.range}（{WeaponPrice(w)}）" + (equipped ? " ●" : ""));
            SetButtonColor(weaponButtons[i], equipped
            ? new Color(0.25f, 0.45f, 0.35f, 1f)
            : new Color(0.20f, 0.22f, 0.28f, 1f));
        }

        // 装備ボタン
        for (int i = 0; i < equipButtons.Count; i++)
        {
            var item = equipmentCatalog[i];
            bool owned = ap != null && ap.items.Contains(item);
            string mark = Unimplemented.Contains(item.itemName) ? " ※未実装" : "";
            SetButtonText(equipButtons[i],
            $"{item.itemName}（{item.price}）{mark}" + (owned ? " ●装備中" : ""));
            SetButtonColor(equipButtons[i], owned
            ? new Color(0.25f, 0.45f, 0.35f, 1f)
            : new Color(0.20f, 0.22f, 0.28f, 1f));
        }

        confirmLabel.text = phaseTeam == 1 ? "チーム1を確定（チーム2の編成へ）" : "編成を確定して対戦開始";
    }

    void Message(string msg) => messageLabel.text = msg;

    // ---------- 選択操作 ----------

    int IndexOfCharacter(CharacterData c)
    {
        for (int i = 0; i < 3; i++)
            if (picks[i] != null && picks[i].character == c) return i;
        return -1;
    }

    void OnCharacterClicked(CharacterData c)
    {
        int existing = IndexOfCharacter(c);
        if (existing == activeSlot)
        {
            picks[activeSlot] = null; // 同じスロットで再クリック＝外す
        }
        else if (existing >= 0)
        {
            // 別のスロットにいる場合は入れ替え（空きスロットなら移動）
            (picks[existing], picks[activeSlot]) = (picks[activeSlot], picks[existing]);
        }
        else
        {
            picks[activeSlot] = new Pick
            {
                character = c,
                weapon = c.defaultWeapon,
                items = c.equipment != null
            ? new List<EquipmentData>(c.equipment)
            : new List<EquipmentData>()
            };
        }
        Message("");
        Refresh();
    }

    void OnWeaponClicked(WeaponData w)
    {
        var p = ActivePick;
        if (p == null || p.character == null) { Message("先に部隊員を選んでください"); return; }
        int diff = WeaponPrice(w) - (p.weapon != null ? WeaponPrice(p.weapon) : 0);
        if (TeamCost() + diff > budget) { Message("資金が足りません"); return; }
        p.weapon = w;
        // 武器の種別が変わって付けられなくなったオプションは外す
        p.items.RemoveAll(i => i.kind == ItemKind.Option && !OptionCompatible(i, w));
        Message("");
        Refresh();
    }

    void OnEquipmentClicked(EquipmentData item)
    {
        var p = ActivePick;
        if (p == null || p.character == null) { Message("先に部隊員を選んでください"); return; }

        if (p.items.Contains(item))
        {
            p.items.Remove(item); // 装備中なら外す（返金）
            Message("");
            Refresh();
            return;
        }
        if (p.items.Count >= 5) { Message("装備枠がいっぱいです（武器＋装備で6枠まで）"); return; }
        if (p.items.Any(i => i.itemName == item.itemName)) { Message("同じ装備は1人1つまでです"); return; }
        if (item.kind == ItemKind.Shield && p.items.Any(i => i.kind == ItemKind.Shield))
        { Message("シールドは1人1つまでです"); return; }
        if (item.kind == ItemKind.Armor && p.items.Any(i => i.kind == ItemKind.Armor))
        { Message("アーマーは1人1つまでです"); return; }
        if (item.kind == ItemKind.Option && !OptionCompatible(item, p.weapon))
        { Message("そのオプションは今の武器には付けられません"); return; }
        if (TeamCost() + item.price > budget) { Message("資金が足りません"); return; }

        p.items.Add(item);
        Message("");
        Refresh();
    }

    // オプションと武器の対応（ルールブック6.2・6.5）
    bool OptionCompatible(EquipmentData item, WeaponData w)
    {
        if (w == null) return false;
        bool meleeOption = item.itemName == "射程強化" || item.itemName == "火力強化" || item.itemName == "毒付与";
        bool shootOption = item.itemName == "追尾弾" || item.itemName == "炸裂弾"
        || item.itemName == "ビーコン弾" || item.itemName == "ロングバレル";
        if (meleeOption) return w.range <= 2;
        if (shootOption) return w.range >= 3;
        return true;
    }

    // ---------- コスト計算 ----------

    int WeaponPrice(WeaponData w)
    {
        if (w == null) return 0;
        switch (w.name)
        {
            case "W_Knife": return 40;
            case "W_Hammer": return 90;
            case "W_Blade": return 70;
            case "W_Handgun": return 45;
            case "W_AR": return 95;
            case "W_Shotgun": return 85;
            case "W_Sniper1": return 120;
            case "W_Sniper2": return 130;
            case "W_Sniper3": return 140;
            case "W_Grenade": return 160;
            default: return 0;
        }
    }

    string WeaponDisplayName(WeaponData w)
    {
        if (w == null) return "なし";
        switch (w.name)
        {
            case "W_Knife": return "ナイフ";
            case "W_Hammer": return "ハンマー";
            case "W_Blade": return "ブレード";
            case "W_Handgun": return "ハンドガン";
            case "W_AR": return "アサルトライフル";
            case "W_Shotgun": return "ショットガン";
            case "W_Sniper1": return "スナイパーライフル①";
            case "W_Sniper2": return "スナイパーライフル②";
            case "W_Sniper3": return "スナイパーライフル③";
            case "W_Grenade": return "グレネードランチャー";
            default: return w.name;
        }
    }

    int PickCost(Pick p)
    {
        if (p == null || p.character == null) return 0;
        int cost = p.weapon != null ? WeaponPrice(p.weapon) : 0;
        foreach (var i in p.items) cost += i.price;
        return cost;
    }

    int TeamCost()
    {
        int total = 0;
        foreach (var p in picks) total += PickCost(p);
        return total;
    }

    // ---------- 確定・開始 ----------

    void OnConfirm()
    {
        for (int i = 0; i < 3; i++)
        {
            if (picks[i] == null || picks[i].character == null)
            { Message("部隊員を3人選んでください"); return; }
        }
        decided[phaseTeam - 1] = picks;
        if (phaseTeam == 1)
        {
            phaseTeam = 2;
            picks = new Pick[3];
            activeSlot = 0;
            Message("");
            Refresh();
            return;
        }
        if (game == null) { Message("Game（GameController）が未設定です"); return; }
        SpawnTeam(1, decided[0]);
        SpawnTeam(2, decided[1]);
        canvas.gameObject.SetActive(false);
        game.enabled = true;
    }

    void StartWithStandardSquads()
    {
        if (spawner == null) { Message("Spawner が未設定のため、標準編成では開始できません"); return; }
        if (game == null) { Message("Game（GameController）が未設定です"); return; }
        canvas.gameObject.SetActive(false);
        spawner.enabled = true;
        game.enabled = true;
    }

    // UnitSpawner の標準編成と同じ配置・色付けでスポーンする
    void SpawnTeam(int team, Pick[] teamPicks)
    {
        Vector2Int[] cells = team == 1
        ? new[] { new Vector2Int(7, 1), new Vector2Int(8, 1), new Vector2Int(9, 1) }
        : new[] { new Vector2Int(8, 16), new Vector2Int(9, 16), new Vector2Int(10, 16) };
        Color teamColor = team == 1
        ? new Color(0.3f, 0.5f, 1f)
        : new Color(1f, 0.35f, 0.3f);
        for (int i = 0; i < 3; i++)
        {
            var p = teamPicks[i];
            // 共有アセットを書き換えないよう、実行時コピーに編成を反映する
            var runtime = Instantiate(p.character);
            runtime.defaultWeapon = p.weapon;
            runtime.equipment = p.items.ToArray();

            var go = Instantiate(unitPrefab);
            var u = go.GetComponent<Unit>();
            u.data = runtime;
            u.team = team;
            u.cell = cells[i];
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.color = Color.Lerp(p.character.color, teamColor, 0.5f);
                sr.sortingOrder = 2;
            }
        }
    }
}
