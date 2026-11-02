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

    // 解決中の一時停止の種類
    private enum PauseKind
    {
        None, Attack, KaedeMove, KaedeAttack, YukiSnipe, SakuraHeal
    }

    // 1体分の指示：移動先＋準備行動
    // ※Noneは「指示なし」の内部的な既定値専用（プレイヤーの選択肢にはない）
    private class Order
    {
        public Vector2Int? moveCell;  // null = 移動しない
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
    private Unit currentAttacker; // 一時停止中のユニット
    private PauseKind pauseKind = PauseKind.None;
    private int ultAttacksLeft = 0; // 疾風斬の残り攻撃回数
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

        // 解決中の一時停止中のSpace＝キャンセル（疾風斬の移動待ちだけは「移動しない」で次へ）
        if (phase == Phase.Resolution && currentAttacker != null
            && kb != null && kb.spaceKey.wasPressedThisFrame)
        {
            if (pauseKind == PauseKind.KaedeMove)
            {
                Debug.Log($"{currentAttacker.displayName} は追加移動しない");
                StartKaedeAttacks();
            }
            else
            {
                Debug.Log("対象選択をキャンセル");
                EndAttackSelection();
            }
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

        // 【指示入力中】制限時間タイマー（有効なときだけ）
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

        // 【指示入力中】Enter＝選択中ユニットのチーム（なければ未完了の先のチーム）が準備完了
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

        // 【解決中】一時停止中：種類に応じたクリック処理
        if (phase == Phase.Resolution && currentAttacker != null)
        {
            HandleResolutionClick(clicked, cell);
            return;
        }

        if (phase == Phase.Resolution) return;

        // 【準備行動の選択待ち】空マスクリック＝防御で確定
        if (choosingAction)
        {
            if (clicked == null) FinalizeOrder(PreparedAction.Defend);
            return;
        }

        // 【指示入力】移動先をクリック → 移動を予約し、準備行動の選択へ
        if (selected != null && movable.Contains(cell))
        {
            Order o = GetOrder(selected);
            o.moveCell = cell;
            PlaceMarker(o, cell);
            EnterActionChoice(cell);
            return;
        }

        // 【指示入力】自分自身をクリック → 移動しない、準備行動の選択へ
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
            Debug.Log($"{clicked.displayName} を選択（EX {clicked.exPoints}/{Unit.MaxEx}）。移動先を指定（自分をクリックで移動しない）");
        }
        else
        {
            Select(null);
        }
    }

    // 解決中の一時停止中のクリック処理（種類別）
    void HandleResolutionClick(Unit clicked, Vector2Int cell)
    {
        switch (pauseKind)
        {
            case PauseKind.KaedeMove:
                // ハイライト内の空マスへ追加移動、自分をクリックで移動しない
                if (clicked == currentAttacker)
                {
                    Debug.Log($"{currentAttacker.displayName} は追加移動しない");
                    StartKaedeAttacks();
                }
                else if (clicked == null && movable.Contains(cell))
                {
                    currentAttacker.cell = cell;
                    currentAttacker.SnapToCell();
                    currentAttacker.movedThisTurn = true;
                    Debug.Log($"{currentAttacker.displayName} は疾風斬で {cell} へ追加移動");
                    StartKaedeAttacks();
                }
                return; // 範囲外のクリックは無視して待ち続ける

            case PauseKind.KaedeAttack:
                if (clicked != null && clicked.team != currentAttacker.team
                    && attackable.Contains(cell))
                {
                    Attack(currentAttacker, clicked);
                    ultAttacksLeft--;
                    if (!gameOver
                        && currentAttacker.gameObject.activeSelf
                        && ultAttacksLeft > 0
                        && AnyEnemyInRange(currentAttacker, currentAttacker.cell))
                    {
                        ShowAttackRangeFrom(currentAttacker, currentAttacker.cell,
                            new Color(1f, 0f, 0f, 0.35f));
                        Debug.Log($"疾風斬：もう1回攻撃できます（残り {ultAttacksLeft} 回）。対象を選んでください");
                        return; // 一時停止を継続
                    }
                }
                else
                {
                    Debug.Log($"{currentAttacker.displayName} は疾風斬の攻撃を終えた");
                }
                EndAttackSelection();
                return;

            case PauseKind.YukiSnipe:
                if (clicked != null && clicked.team != currentAttacker.team
                    && attackable.Contains(cell))
                {
                    Attack(currentAttacker, clicked, true, 1.5f);
                }
                else
                {
                    Debug.Log($"{currentAttacker.displayName} は必中狙撃を撃たなかった（EXは消費済み）");
                }
                EndAttackSelection();
                return;

            case PauseKind.SakuraHeal:
                if (clicked != null && clicked.team == currentAttacker.team)
                {
                    HealUlt(currentAttacker, clicked);
                }
                else
                {
                    Debug.Log($"{currentAttacker.displayName} は緊急治療を使わなかった（EXは消費済み）");
                }
                EndAttackSelection();
                return;

            default: // PauseKind.Attack（通常攻撃の対象選択）
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
        string ultHint = selected.exPoints >= 1
            ? "[R]ULT"
            : "[R]ULT（EX不足で選択不可）";
        Debug.Log($"{selected.displayName}：準備行動を選んでください " +
                  $"[Q]攻撃 [W]防御 [E]特殊装備 {ultHint}");
    }

    // ボタンから呼ばれる選択肢
    public void ChooseAttack() { if (choosingAction) FinalizeOrder(PreparedAction.Attack); }
    public void ChooseDefend() { if (choosingAction) FinalizeOrder(PreparedAction.Defend); }
    public void ChooseSpecial() { if (choosingAction) FinalizeOrder(PreparedAction.Special); }
    public void ChooseUlt() { if (choosingAction) FinalizeOrder(PreparedAction.Ult); }

    void FinalizeOrder(PreparedAction action)
    {
        // ULT準備は、指示時点でEXを持っている場合のみ選択できる
        if (action == PreparedAction.Ult && selected.exPoints < 1)
        {
            Debug.Log($"{selected.displayName} はEXを持っていないため、ULT準備を選べません。別の準備行動を選んでください");
            return; // 確定せず、行動選択モードのまま
        }

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

        // 鉄壁（ゴウのULT）も解決開始時点で発動
        foreach (var kv in orders)
        {
            Unit u = kv.Key;
            if (kv.Value.prepared == PreparedAction.Ult
                && u.data != null && u.data.characterName == "ゴウ")
            {
                if (u.exPoints >= 1)
                {
                    u.exPoints--;
                    u.ultGuarding = true;
                    Debug.Log($"{u.displayName} のULT「鉄壁」発動！（EX {u.exPoints}/{Unit.MaxEx}）半径2マス以内の味方への攻撃を引き受け、受けるダメージ半減");
                }
                else
                {
                    Debug.Log($"{u.displayName} のULT「鉄壁」はEX不足で不発");
                }
            }
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

            // 行動不能：移動も行動もできない
            if (u.stunTurns > 0)
            {
                u.stunTurns--;
                u.movedThisTurn = false;
                Debug.Log($"{u.displayName} は行動不能で動けない（残り {u.stunTurns} ターン）");
                resolveIndex++;
                continue;
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

                    // EXマス：移動して止まるとEX+1（上限2。居座りでは増えない）
                    if (terrain != null
                        && terrain.GetTerrain(u.cell) == TerrainType.Ex)
                    {
                        if (u.exPoints < Unit.MaxEx)
                        {
                            u.exPoints++;
                            Debug.Log($"{u.displayName} はEXマスに止まった：EX {u.exPoints}/{Unit.MaxEx}");
                        }
                        else
                        {
                            Debug.Log($"{u.displayName} はEXマスに止まった（EXは上限の{Unit.MaxEx}）");
                        }
                    }
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
                if (ActivateUlt(u)) return; // 一時停止（対象選択など）
                resolveIndex++;
                continue;
            }
            if (action == PreparedAction.Attack)
            {
                if (AnyEnemyInRange(u, u.cell))
                {
                    // 対象選択のため一時停止
                    BeginPause(u, PauseKind.Attack);
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

    // 一時停止の開始（ユニットを黄色くハイライト）
    void BeginPause(Unit u, PauseKind kind)
    {
        currentAttacker = u;
        pauseKind = kind;
        var sr = u.GetComponent<SpriteRenderer>();
        attackerOriginalColor = sr.color;
        attackerOriginalScale = u.transform.localScale;
        sr.color = Color.yellow;
        u.transform.localScale = attackerOriginalScale * 1.2f;
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
        pauseKind = PauseKind.None;
        ultAttacksLeft = 0;
        ClearHighlights();
        resolveIndex++;
        if (!gameOver) AdvanceResolution();
    }

    // 疾風斬：追加移動のあと、近接攻撃フェイズへ
    void StartKaedeAttacks()
    {
        ultAttacksLeft = 2;
        if (AnyEnemyInRange(currentAttacker, currentAttacker.cell))
        {
            pauseKind = PauseKind.KaedeAttack;
            ShowAttackRangeFrom(currentAttacker, currentAttacker.cell,
                new Color(1f, 0f, 0f, 0.35f));
            Debug.Log($"{currentAttacker.displayName} の疾風斬：攻撃対象を選んでください（残り {ultAttacksLeft} 回）");
        }
        else
        {
            Debug.Log($"{currentAttacker.displayName} の疾風斬：射程内に敵がおらず攻撃なし");
            EndAttackSelection();
        }
    }

    // ULTの発動を試みる。一時停止したら true を返す
    bool ActivateUlt(Unit u)
    {
        string charName = u.data != null ? u.data.characterName : "";
        string ultName = u.data != null ? u.data.ultName : "ULT";

        // 鉄壁は解決開始時に発動済み（StartResolution）
        if (charName == "ゴウ")
        {
            Debug.Log(u.ultGuarding
                ? $"{u.displayName} は鉄壁を維持している"
                : $"{u.displayName} のULTは不発だった");
            return false;
        }

        if (u.exPoints < 1)
        {
            Debug.Log($"{u.displayName} のULT「{ultName}」はEX不足で不発（EX {u.exPoints}）");
            return false;
        }

        switch (charName)
        {
            case "カエデ": // 疾風斬：追加移動4マス＋近接攻撃2回
                u.exPoints--;
                Debug.Log($"{u.displayName} のULT「{ultName}」発動！（EX {u.exPoints}/{Unit.MaxEx}）追加で最大4マス移動し、近接攻撃を2回行える");
                BeginPause(u, PauseKind.KaedeMove);
                ShowMoveRangeFrom(u, u.cell, 4);
                Debug.Log("追加の移動先を選んでください（自分をクリックかSpaceで移動しない）");
                return true;

            case "ユキ": // 必中狙撃：必中・ダメージ+50%の射撃をこの行動で撃つ
                if (!AnyEnemyInRange(u, u.cell))
                {
                    Debug.Log($"{u.displayName} のULT「{ultName}」：射程内に敵がおらず不発（EXは消費しない）");
                    return false;
                }
                u.exPoints--;
                Debug.Log($"{u.displayName} のULT「{ultName}」発動！（EX {u.exPoints}/{Unit.MaxEx}）必中・ダメージ+50%の射撃");
                BeginPause(u, PauseKind.YukiSnipe);
                ShowAttackRangeFrom(u, u.cell,
                    new Color(1f, 0f, 0f, 0.35f));
                Debug.Log("狙撃する対象を選んでください");
                return true;

            case "サクラ": // 緊急治療：味方1人を回復30＋状態異常全解除（射程なし）
                u.exPoints--;
                Debug.Log($"{u.displayName} のULT「{ultName}」発動！（EX {u.exPoints}/{Unit.MaxEx}）回復する味方をクリック（自分も可）");
                BeginPause(u, PauseKind.SakuraHeal);
                return true;

            case "ハヤテ": // 広域スキャン：半径6の敵をログで把握（目視システム実装までの代用）
                u.exPoints--;
                Debug.Log($"{u.displayName} のULT「{ultName}」発動！（EX {u.exPoints}/{Unit.MaxEx}）半径6マス以内の敵をスキャン");
                bool found = false;
                foreach (Unit e in FindObjectsByType<Unit>())
                {
                    if (e.team == u.team) continue;
                    if (Vector2.Distance(u.cell, e.cell) <= 6f)
                    {
                        Debug.Log($"　スキャン：{e.displayName}（HP {e.hp}）at {e.cell}");
                        found = true;
                    }
                }
                if (!found) Debug.Log("　スキャン範囲に敵はいなかった");
                return false;

            default: // ノイ・レン・ミコトは依存システムのステップで実装
                Debug.Log($"{u.displayName} のULT「{ultName}」の効果は後続ステップで実装（EXは消費しない）");
                return false;
        }
    }

    // 緊急治療（サクラのULT）
    void HealUlt(Unit healer, Unit target)
    {
        int before = target.hp;
        target.hp = Mathf.Min(target.maxHp, target.hp + 30);
        bool cured = target.poisoned || target.beaconed || target.stunTurns > 0;
        target.poisoned = false;
        target.beaconed = false;
        target.stunTurns = 0;
        target.poisonSourceTeam = 0;
        Debug.Log($"{healer.displayName} のULT「緊急治療」：{target.displayName} のHP {before} → {target.hp}{(cured ? "、状態異常をすべて解除" : "")}");
    }

    // 鉄壁：対象の半径2マス以内に、攻撃を引き受けられる味方がいるか
    Unit FindGuardFor(Unit target)
    {
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u == target) continue;
            if (u.team != target.team) continue;
            if (!u.ultGuarding) continue;
            if (Vector2.Distance(u.cell, target.cell) <= 2f) return u;
        }
        return null;
    }

    void FinishRound()
    {
        // ターン終了処理：毒ダメージ
        ApplyPoisonDamage();
        if (gameOver) { RefreshReadyUI(); return; }

        orders.Clear();
        teamReady[1] = false;
        teamReady[2] = false;
        orderTimer = orderTimeLimit;
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            u.movedThisTurn = false;
            u.defending = false;
            u.ultGuarding = false;
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
                turnText.text = $"Score {score[1]} - {score[2]} : {result}";
            RefreshReadyUI();
            return;
        }
        phase = Phase.Orders;
        Debug.Log($"―― ターン{turnNumber}：指示入力（両チーム同時） ――");

        // ターン4・8・12の指示開始時点：全ユニットにEX+1（保持上限2）
        if (turnNumber % 4 == 0)
        {
            foreach (Unit u in FindObjectsByType<Unit>())
            {
                if (u.exPoints < Unit.MaxEx) u.exPoints++;
            }
            Debug.Log($"ターン{turnNumber} 開始ボーナス：全ユニットのEX+1（上限 {Unit.MaxEx}）");
        }

        UpdateTurnText();
        RefreshReadyUI();
    }

    // ターン終了処理：毒状態のユニットに固定5ダメージ
    void ApplyPoisonDamage()
    {
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (!u.poisoned) continue;
            u.hp -= 5;
            Debug.Log($"{u.displayName} は毒で5ダメージ（残りHP {Mathf.Max(u.hp, 0)}）");
            if (u.hp <= 0)
            {
                Debug.Log($"{u.displayName} は毒で倒れた！");
                u.gameObject.SetActive(false);
                if (u.poisonSourceTeam >= 1 && u.poisonSourceTeam <= 2)
                {
                    score[u.poisonSourceTeam] += 2;
                    Debug.Log($"チーム{u.poisonSourceTeam} +2点（スコア {score[1]} - {score[2]}）");
                }
            }
        }
        CheckWin();
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
                ? $"  ({Mathf.CeilToInt(Mathf.Max(orderTimer, 0f))}s)"
                : "";
            turnText.text = $"Turn {turnNumber} - Orders  T1:{r1} T2:{r2}{timer}";
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

    // 指定した名前の装備を持っているか
    bool HasItem(Unit unit, string itemName)
    {
        if (unit.data == null || unit.data.equipment == null) return false;
        foreach (var item in unit.data.equipment)
        {
            if (item != null && item.itemName == itemName) return true;
        }
        return false;
    }

    void Attack(Unit attacker, Unit target,
                bool guaranteedHit = false, float damageMultiplier = 1f)
    {
        // 鉄壁：半径2マス以内の味方への攻撃をゴウが引き受ける
        Unit guard = FindGuardFor(target);
        if (guard != null)
        {
            Debug.Log($"{guard.displayName} の鉄壁！ {target.displayName} への攻撃を引き受けた");
            target = guard;
        }

        // ハンマーはシールドの防御補正とブロックを無視する
        bool isHammer = attacker.weapon != null
                        && attacker.weapon.name == "W_Hammer";

        int hitRate = Mathf.Clamp(
            Mathf.RoundToInt(attacker.weaponAccuracy
                + (attacker.precision - target.agility) / 2f
                + (attacker.movedThisTurn ? 0 : 10)),
            5, 95);

        bool hit;
        if (guaranteedHit)
        {
            hit = true;
            Debug.Log($"{attacker.displayName} の攻撃！ 必中のため命中確定");
        }
        else
        {
            int roll = Random.Range(1, 101);
            hit = roll <= hitRate;
            Debug.Log(hit
                ? $"{attacker.displayName} の攻撃！ 命中率 {hitRate}% → 出目 {roll} で 命中"
                : $"{attacker.displayName} の攻撃！ 命中率 {hitRate}% → 出目 {roll} で 外れ");
        }

        if (hit)
        {
            int defBonus = target.armorBonus
                           - (isHammer ? target.shieldBonus : 0);
            if (isHammer && (target.shieldBonus > 0 || target.shieldBlockRate > 0))
                Debug.Log("ハンマーはシールドの防御補正とブロックを無視");

            float raw = (attacker.attack + attacker.weaponPower) / 2f
                        * 100f / (100 + target.defense + defBonus);
            if (!attacker.movedThisTurn) raw *= 1.1f;
            raw *= damageMultiplier;
            int damage = Mathf.Max(1, Mathf.RoundToInt(raw));

            // 鉄壁発動中：受けるダメージ半減
            if (target.ultGuarding)
            {
                damage = Mathf.Max(1, Mathf.RoundToInt(damage * 0.5f));
                Debug.Log($"{target.displayName} の鉄壁でダメージ半減 → {damage}");
            }

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

            // 状態異常の付与（ダメージが通って、対象が生き残った場合のみ）
            if (target.hp > 0)
            {
                if (HasItem(attacker, "毒付与") && !target.poisoned)
                {
                    target.poisoned = true;
                    target.poisonSourceTeam = attacker.team;
                    Debug.Log($"{target.displayName} は毒状態になった！");
                }
                if (HasItem(attacker, "ビーコン弾") && !target.beaconed)
                {
                    target.beaconed = true;
                    Debug.Log($"{target.displayName} にビーコンが付着した！（壁抜き等の効果は情報戦ステップで実装）");
                }
            }

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

    // 指定した起点・半径の移動範囲だけを表示（ULTの追加移動用）
    void ShowMoveRangeFrom(Unit unit, Vector2Int origin, int radius)
    {
        ClearHighlights();
        Unit[] all = FindObjectsByType<Unit>();
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector2Int c = origin + new Vector2Int(dx, dy);
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
