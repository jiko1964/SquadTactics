using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class GameController : MonoBehaviour
{
    public GameObject highlightPrefab;
    public TextMeshProUGUI turnText;
    public GameObject actionPanel;

    [Header("準備完了ボタン（画面右）")]
    public Button team1ReadyButton;
    public Button team2ReadyButton;
    public TextMeshProUGUI team1ReadyLabel;
    public TextMeshProUGUI team2ReadyLabel;

    [Header("指示入力の制限時間（秒） 0=無制限")]
    public float orderTimeLimit = 0f;

    private enum Phase { Orders, Resolution }
    private enum PreparedAction { None, Attack, Defend, Special, Ult }

    // 1体分の指示：移動先＋準備行動
    // ※Noneは「指示なし」の内部的な既定値専用（プレイヤーの選択肢にはない）
    private class Order
    {
        public Vector2Int? moveCell; // null = 移動しない
        public PreparedAction prepared = PreparedAction.None;
        public GameObject marker;
    }

    private Unit selected;
    private Color originalColor;
    private Vector3 originalScale;
    private readonly List<GameObject> highlights = new();
    private readonly HashSet<Vector2Int> movable = new();
    private readonly HashSet<Vector2Int> attackable = new();
    private readonly Dictionary<Unit, Order> orders = new();

    public int turnNumber = 1;
    private Phase phase = Phase.Orders;
    private bool gameOver = false;
    private readonly int[] score = new int[3];
    private TerrainGrid terrain;

    // 同時入力：チームごとの準備完了フラグと制限時間タイマー
    private readonly bool[] teamReady = new bool[3];
    private float orderTimer = 0f;

    // 指示入力用：移動先を決めた後、準備行動の選択待ちか
    private bool choosingAction = false;

    // 解決用
    private List<Unit> initiative = new();
    private int resolveIndex = 0;
    private Unit currentAttacker; // 対象選択待ちのユニット
    private Color attackerOriginalColor;
    private Vector3 attackerOriginalScale;

    private const int MaxTurns = 12;

    void Start()
    {
        terrain = FindAnyObjectByType<TerrainGrid>();
        if (actionPanel != null) actionPanel.SetActive(false);
        orderTimer = orderTimeLimit;
        UpdateTurnText();
        RefreshReadyUI();
        Debug.Log("―― ターン1：指示入力（両チーム同時） ――");
        Debug.Log("全ユニットの指示を出すか、Enter／右のボタンで準備完了すると解決フェイズへ移行します");
    }

    void Update()
    {
        if (gameOver) return;

        var kb = Keyboard.current;

        // 対象選択待ちのときのSpace＝攻撃をスキップ
        if (phase == Phase.Resolution && currentAttacker != null
        && kb != null && kb.spaceKey.wasPressedThisFrame)
        {
            Debug.Log($"{currentAttacker.displayName} は攻撃しない");
            EndAttackSelection();
            return;
        }

        // 準備行動の選択待ち（Q/W/E/Rキー）
        if (choosingAction && selected != null && kb != null)
        {
            if (kb.qKey.wasPressedThisFrame) { FinalizeOrder(PreparedAction.Attack); return; }
            if (kb.wKey.wasPressedThisFrame) { FinalizeOrder(PreparedAction.Defend); return; }
            if (kb.eKey.wasPressedThisFrame) { FinalizeOrder(PreparedAction.Special); return; }
            if (kb.rKey.wasPressedThisFrame) { FinalizeOrder(PreparedAction.Ult); return; }
        }

        // 制限時間タイマー（有効なときだけ）
        if (phase == Phase.Orders && orderTimeLimit > 0f && !choosingAction)
        {
            orderTimer -= Time.deltaTime;
            UpdateTurnText();
            if (orderTimer <= 0f)
            {
                Debug.Log("指示入力の制限時間が切れました。解決フェイズへ移行します");
                StartResolution();
                return;
            }
        }

        // Enter＝選択中ユニットのチーム（なければ未完了の先のチーム）が準備完了
        if (phase == Phase.Orders && !choosingAction && kb != null
        && (kb.enterKey.wasPressedThisFrame
        || kb.numpadEnterKey.wasPressedThisFrame))
        {
            int team = selected != null
            ? selected.team
            : (teamReady[1] ? 2 : 1);
            ReadyTeam(team);
            return;
        }

        if (Mouse.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        // UIの上でのクリック（ボタン・パネル）は盤面クリックとして扱わない
        if (EventSystem.current != null
        && EventSystem.current.IsPointerOverGameObject())
            return;

        Vector2 screenPos = Mouse.current.position.ReadValue();
        Vector3 world = Camera.main.ScreenToWorldPoint(
        new Vector3(screenPos.x, screenPos.y, 0));
        Vector2Int cell = new Vector2Int(
        Mathf.FloorToInt(world.x), Mathf.FloorToInt(world.y));
        Unit clicked = FindUnitAt(cell);

        // 対象選択待ち：敵をクリックで攻撃、空マスでスキップ
        if (phase == Phase.Resolution && currentAttacker != null)
        {
            if (clicked != null && clicked.team != currentAttacker.team
            && attackable.Contains(cell))
            {
                Attack(currentAttacker, clicked);
            }
            else
            {
                Debug.Log($"{currentAttacker.displayName} は攻撃しない");
            }
            EndAttackSelection();
            return;
        }

        if (phase == Phase.Resolution) return;

        // 空マスクリック＝防御で確定
        if (choosingAction)
        {
            if (clicked == null) FinalizeOrder(PreparedAction.Defend);
            return;
        }

        // 移動先をクリック → 移動を予約し、準備行動の選択へ
        if (selected != null && movable.Contains(cell))
        {
            Order o = GetOrder(selected);
            o.moveCell = cell;
            PlaceMarker(o, cell);
            EnterActionChoice(cell);
            return;
        }

        // 自分自身をクリック → 移動しない、準備行動の選択へ
        if (selected != null && clicked == selected)
        {
            Order o = GetOrder(selected);
            o.moveCell = null;
            ClearMarker(o);
            EnterActionChoice(selected.cell);
            return;
        }

        // 選択（両チーム可。準備完了済みチームは変更不可）
        if (clicked != null)
        {
            if (teamReady[clicked.team])
            {
                Debug.Log($"チーム{clicked.team} は準備完了済みのため、指示を変更できません");
                return;
            }
            ClearOrder(clicked);
            Select(clicked);
            Debug.Log($"{clicked.displayName} を選択。移動先を指定（自分をクリックで移動しない）");
        }
        else
        {
            Select(null);
        }
    }

    // 画面右の準備完了ボタンから呼ばれる
    public void ReadyTeam1Button()
    {
        if (phase == Phase.Orders && !choosingAction) ReadyTeam(1);
    }

    public void ReadyTeam2Button()
    {
        if (phase == Phase.Orders && !choosingAction) ReadyTeam(2);
    }

    // 準備完了：そのチームの指示をロックし、両チームそろえば解決開始
    void ReadyTeam(int team)
    {
        if (teamReady[team]) return;
        teamReady[team] = true;
        if (selected != null && selected.team == team) Select(null);
        Debug.Log($"チーム{team} が準備完了");
        UpdateTurnText();
        RefreshReadyUI();

        if (teamReady[1] && teamReady[2])
        {
            Debug.Log("両チーム準備完了。解決フェイズへ移行します");
            StartResolution();
        }
    }

    // チームの全ユニットに指示が出ているか
    bool AllUnitsOrdered(int team)
    {
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u.team == team && !orders.ContainsKey(u)) return false;
        }
        return true;
    }

    // 準備完了ボタンの表示を状態に合わせる
    void RefreshReadyUI()
    {
        SetReadyButton(team1ReadyButton, team1ReadyLabel, 1);
        SetReadyButton(team2ReadyButton, team2ReadyLabel, 2);
    }

    void SetReadyButton(Button button, TextMeshProUGUI label, int team)
    {
        if (label != null)
            label.text = teamReady[team]
            ? $"Team {team}: READY"
            : $"Team {team} Ready";
        if (button != null)
        {
            var img = button.GetComponent<Image>();
            if (img != null)
                img.color = teamReady[team]
                ? new Color(0.3f, 0.75f, 0.35f)
                : Color.white;
        }
    }

    // 移動先が決まったら準備行動の選択モードに入る（パネル表示）
    void EnterActionChoice(Vector2Int origin)
    {
        choosingAction = true;
        if (actionPanel != null) actionPanel.SetActive(true);
        ShowAttackRangeFrom(selected, origin,
        new Color(1f, 0f, 0f, 0.3f));
        Debug.Log($"{selected.displayName}：準備行動を選んでください " +
        "[Q]攻撃 [W]防御 [E]特殊装備 [R]ULT");
    }

    // ボタンから呼ばれる選択肢
    public void ChooseAttack() { if (choosingAction) FinalizeOrder(PreparedAction.Attack); }
    public void ChooseDefend() { if (choosingAction) FinalizeOrder(PreparedAction.Defend); }
    public void ChooseSpecial() { if (choosingAction) FinalizeOrder(PreparedAction.Special); }
    public void ChooseUlt() { if (choosingAction) FinalizeOrder(PreparedAction.Ult); }

    void FinalizeOrder(PreparedAction action)
    {
        Unit unit = selected;
        Order o = GetOrder(unit);
        o.prepared = action;
        SetMarkerColor(o, action);
        string label = action switch
        {
            PreparedAction.Attack => "攻撃準備",
            PreparedAction.Defend => "防御準備",
            PreparedAction.Special => "特殊装備の使用準備",
            PreparedAction.Ult => "ULT準備",
            _ => "行動なし",
        };
        Debug.Log($"{unit.displayName} の指示確定：{label}");
        choosingAction = false;
        if (actionPanel != null) actionPanel.SetActive(false);
        Select(null);

        // そのチームの全ユニットの指示がそろったら自動で準備完了
        if (AllUnitsOrdered(unit.team))
        {
            Debug.Log($"チーム{unit.team} の全ユニットの指示が完了したため、自動で準備完了にします");
            ReadyTeam(unit.team);
        }
    }

    List<Unit> BuildInitiative()
    {
        var list = new List<Unit>(FindObjectsByType<Unit>());
        var tie = new Dictionary<Unit, int>();
        foreach (var u in list) tie[u] = Random.Range(0, 100000);
        list.Sort((a, b) =>
        {
            int c = b.agility.CompareTo(a.agility);
            if (c != 0) return c;
            c = b.mobility.CompareTo(a.mobility);
            if (c != 0) return c;
            return tie[a].CompareTo(tie[b]);
        });
        return list;
    }

    void StartResolution()
    {
        Select(null);
        choosingAction = false;
        if (actionPanel != null) actionPanel.SetActive(false);

        phase = Phase.Resolution;
        initiative = BuildInitiative();
        var names = new List<string>();
        foreach (var u in initiative) names.Add(u.displayName);
        Debug.Log($"―― ターン{turnNumber} 解決開始 ――");
        Debug.Log("イニシアチブ順：" + string.Join(" → ", names));
        UpdateTurnText();

        foreach (var kv in orders) ClearMarker(kv.Value);

        // 防御準備は解決開始時点で有効化（そのターン受ける全攻撃がブロック対象）
        foreach (var kv in orders)
        {
            if (kv.Value.prepared == PreparedAction.Defend)
                kv.Key.defending = true;
        }

        resolveIndex = 0;
        AdvanceResolution();
    }

    // イニシアチブ順に1体ずつ処理する（対象選択が必要な所で一時停止）
    void AdvanceResolution()
    {
        while (!gameOver && resolveIndex < initiative.Count)
        {
            Unit u = initiative[resolveIndex];
            if (!u.gameObject.activeSelf)
            {
                resolveIndex++;
                continue; // 倒れたユニットの指示は無効
            }
            orders.TryGetValue(u, out var o);

            // ① 移動を実行
            u.movedThisTurn = false;
            if (o != null && o.moveCell.HasValue && o.moveCell.Value != u.cell)
            {
                Vector2Int dest = o.moveCell.Value;
                if ((terrain == null || terrain.IsPassable(dest))
                && FindUnitAt(dest) == null)
                {
                    u.cell = dest;
                    u.SnapToCell();
                    u.movedThisTurn = true;
                    Debug.Log($"{u.displayName} は {dest} へ移動");
                }
                else
                {
                    Debug.Log($"{u.displayName} の移動は失敗（到着先が塞がれている）");
                }
            }

            // ② 準備行動を試みる
            PreparedAction action = o?.prepared ?? PreparedAction.None;
            if (action == PreparedAction.Defend)
            {
                Debug.Log(u.shieldBlockRate > 0
                ? $"{u.displayName} は防御態勢（ブロック率 {u.shieldBlockRate}%）"
                : $"{u.displayName} は防御態勢（シールドなし：ブロック不可）");
                resolveIndex++;
                continue;
            }
            if (action == PreparedAction.Special)
            {
                var names = new List<string>();
                if (u.data != null && u.data.equipment != null)
                {
                    foreach (var item in u.data.equipment)
                    {
                        if (item != null && item.kind == ItemKind.Special)
                            names.Add(item.itemName);
                    }
                }
                Debug.Log($"{u.displayName} の特殊装備使用：{(names.Count > 0 ? string.Join("・", names) : "（装備なし）")} ※効果は今後のステップで実装");
                resolveIndex++;
                continue;
            }
            if (action == PreparedAction.Ult)
            {
                Debug.Log($"{u.displayName} のULTは未実装のためスキップ");
                resolveIndex++;
                continue;
            }
            if (action == PreparedAction.Attack)
            {
                if (AnyEnemyInRange(u, u.cell))
                {
                    // 対象選択のため一時停止
                    currentAttacker = u;
                    var sr = u.GetComponent<SpriteRenderer>();
                    attackerOriginalColor = sr.color;
                    attackerOriginalScale = u.transform.localScale;
                    sr.color = Color.yellow;
                    u.transform.localScale = attackerOriginalScale * 1.2f;
                    ShowAttackRangeFrom(u, u.cell,
                    new Color(1f, 0f, 0f, 0.35f));
                    Debug.Log($"{u.displayName} の攻撃：対象を選んでください（攻撃しない場合は空マスかSpace）");
                    return;
                }
                Debug.Log($"{u.displayName} の攻撃準備：射程内に敵がおらずスキップ");
                resolveIndex++;
                continue;
            }

            resolveIndex++; // 行動なし（指示なしユニット）
        }

        if (!gameOver) FinishRound();
    }

    void EndAttackSelection()
    {
        if (currentAttacker != null)
        {
            var sr = currentAttacker.GetComponent<SpriteRenderer>();
            sr.color = attackerOriginalColor;
            currentAttacker.transform.localScale = attackerOriginalScale;
            currentAttacker = null;
        }
        ClearHighlights();
        resolveIndex++;
        if (!gameOver) AdvanceResolution();
    }

    void FinishRound()
    {
        orders.Clear();
        teamReady[1] = false;
        teamReady[2] = false;
        orderTimer = orderTimeLimit;
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            u.movedThisTurn = false;
            u.defending = false;
        }
        turnNumber++;
        if (turnNumber > MaxTurns)
        {
            gameOver = true;
            string result = score[1] == score[2]
            ? "Draw"
            : $"Team {(score[1] > score[2] ? 1 : 2)} Wins!";
            Debug.Log($"最終ターン終了。スコア {score[1]} - {score[2]}");
            if (turnText != null)
                turnText.text = $"Score {score[1]} - {score[2]}: {result}";
            RefreshReadyUI();
            return;
        }
        phase = Phase.Orders;
        Debug.Log($"―― ターン{turnNumber}：指示入力（両チーム同時） ――");
        UpdateTurnText();
        RefreshReadyUI();
    }

    bool AnyEnemyInRange(Unit unit, Vector2Int origin)
    {
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u.team == unit.team) continue;
            if (Vector2.Distance(origin, u.cell) <= unit.weaponRange)
                return true;
        }
        return false;
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
            Debug.Log($"チーム{winner} の勝利！（スコア {score[1]} - {score[2]}）");
            if (turnText != null) turnText.text = $"Team {winner} Wins!";
        }
    }

    void UpdateTurnText()
    {
        if (turnText == null) return;
        if (phase == Phase.Resolution)
        {
            turnText.text = $"Turn {turnNumber} - Resolution";
        }
        else
        {
            string r1 = teamReady[1] ? "Ready" : "...";
            string r2 = teamReady[2] ? "Ready" : "...";
            string timer = orderTimeLimit > 0f
            ? $" ({Mathf.CeilToInt(Mathf.Max(orderTimer, 0f))}s)"
            : "";
            turnText.text = $"Turn {turnNumber} - Orders T1:{r1} T2:{r2}{timer}";
        }
    }

    Order GetOrder(Unit u)
    {
        if (!orders.TryGetValue(u, out var o))
        {
            o = new Order();
            orders[u] = o;
        }
        return o;
    }

    void ClearOrder(Unit u)
    {
        if (orders.TryGetValue(u, out var o))
        {
            ClearMarker(o);
            orders.Remove(u);
        }
    }

    void PlaceMarker(Order o, Vector2Int cell)
    {
        ClearMarker(o);
        o.marker = Instantiate(highlightPrefab);
        o.marker.transform.position = new Vector3(cell.x + 0.5f, cell.y + 0.5f, 0);
        o.marker.GetComponent<SpriteRenderer>().color
        = new Color(0.9f, 0.9f, 0.9f, 0.55f);
    }

    void SetMarkerColor(Order o, PreparedAction action)
    {
        if (o.marker == null) return;
        o.marker.GetComponent<SpriteRenderer>().color = action switch
        {
            PreparedAction.Attack => new Color(0.2f, 0.9f, 0.3f, 0.55f),
            PreparedAction.Defend => new Color(0.3f, 0.5f, 1f, 0.55f),
            PreparedAction.Special => new Color(0.95f, 0.6f, 0.15f, 0.55f),
            PreparedAction.Ult => new Color(0.7f, 0.3f, 0.95f, 0.55f),
            _ => new Color(0.6f, 0.6f, 0.6f, 0.55f),
        };
    }

    void ClearMarker(Order o)
    {
        if (o.marker != null)
        {
            Destroy(o.marker);
            o.marker = null;
        }
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
        // ハンマーはシールドの防御補正とブロックを無視する
        bool isHammer = attacker.weapon != null
        && attacker.weapon.name == "W_Hammer";

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
            int defBonus = target.armorBonus
            - (isHammer ? target.shieldBonus : 0);
            if (isHammer && (target.shieldBonus > 0 || target.shieldBlockRate > 0))
                Debug.Log("ハンマーはシールドの防御補正とブロックを無視");

            float raw = (attacker.attack + attacker.weaponPower) / 2f
            * 100f / (100 + target.defense + defBonus);
            if (!attacker.movedThisTurn) raw *= 1.1f;
            int damage = Mathf.Max(1, Mathf.RoundToInt(raw));

            Debug.Log($"{attacker.displayName} の攻撃！ 命中率 {hitRate}% → 出目 {roll} で 命中");

            // シールドのブロック判定（防御準備中のみ、ハンマーは対象外）
            if (target.defending && target.shieldBlockRate > 0 && !isHammer)
            {
                int blockRoll = Random.Range(1, 101);
                if (blockRoll <= target.shieldBlockRate)
                {
                    Debug.Log($"{target.displayName} のシールドがブロック成功（出目 {blockRoll} ≤ {target.shieldBlockRate}%）→ ダメージ無効");

                    // カウンターシールド：軽減後ダメージの一部を反射
                    if (target.hasCounterShield)
                    {
                        bool melee = attacker.weaponRange <= 2;
                        int reflect = Mathf.RoundToInt(
                        damage * (melee ? 0.75f : 0.5f));
                        if (reflect > 0)
                        {
                            attacker.hp -= reflect;
                            Debug.Log($"カウンターシールドの反射！ {attacker.displayName} に {reflect} ダメージ（残りHP {Mathf.Max(attacker.hp, 0)}）");
                            if (attacker.hp <= 0)
                            {
                                Debug.Log($"{attacker.displayName} を撃破！");
                                attacker.gameObject.SetActive(false);
                                score[target.team] += 2;
                                Debug.Log($"チーム{target.team} +2点（スコア {score[1]} - {score[2]}）");
                            }
                        }
                    }

                    attacker.hasAttacked = true;
                    CheckWin();
                    return;
                }
                Debug.Log($"{target.displayName} のブロック失敗（出目 {blockRoll} > {target.shieldBlockRate}%）");
            }

            target.hp -= damage;
            Debug.Log($"{target.displayName} に {damage} ダメージ（残りHP {Mathf.Max(target.hp, 0)}）");
            if (target.hp <= 0)
            {
                Debug.Log($"{target.displayName} を撃破！");
                target.gameObject.SetActive(false);
                score[attacker.team] += 2;
                Debug.Log($"チーム{attacker.team} +2点（スコア {score[1]} - {score[2]}）");
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

    // 指示入力用：移動範囲＋薄い攻撃範囲（現在位置から）
    void ShowRanges(Unit unit)
    {
        ClearHighlights();

        int r = unit.MoveTiles;
        Unit[] all = FindObjectsByType<Unit>();
        for (int dx = -r; dx <= r; dx++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector2Int c = unit.cell + new Vector2Int(dx, dy);
                if (!Inside(c)) continue;
                if (terrain != null && !terrain.IsPassable(c)) continue;

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

        for (int dx = -unit.weaponRange; dx <= unit.weaponRange; dx++)
        {
            for (int dy = -unit.weaponRange; dy <= unit.weaponRange; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector2Int c = unit.cell + new Vector2Int(dx, dy);
                if (!Inside(c)) continue;
                if (Vector2.Distance(unit.cell, c) > unit.weaponRange) continue;

                attackable.Add(c);
                AddHighlight(c, new Color(1f, 0f, 0f, 0.15f));
            }
        }
    }

    // 指定した起点からの攻撃範囲だけを表示
    void ShowAttackRangeFrom(Unit unit, Vector2Int origin, Color color)
    {
        ClearHighlights();
        for (int dx = -unit.weaponRange; dx <= unit.weaponRange; dx++)
        {
            for (int dy = -unit.weaponRange; dy <= unit.weaponRange; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector2Int c = origin + new Vector2Int(dx, dy);
                if (!Inside(c)) continue;
                if (Vector2.Distance(origin, c) > unit.weaponRange) continue;

                attackable.Add(c);
                AddHighlight(c, color);
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
