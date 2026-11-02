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
        None, Attack, KaedeMove, KaedeAttack, YukiSnipe, SakuraHeal, Grenade,
        TrapPlace, SmokePlace, DroneAttack, HackTarget, DroneDummyTarget,
        HackChoice, DroneCreateType, DronePlace
    }

    // 1体分の指示：移動先＋準備行動
    // ※Noneは「指示なし」の内部的な既定値専用（プレイヤーの選択肢にはない）
    private class Order
    {
        public Vector2Int? moveCell;  // null = 移動しない
        public PreparedAction prepared = PreparedAction.None;
        public string specialChoice;  // 特殊装備準備で選んだ装備名（切除を含む）
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

    // ネクサス（中央運河では中央の橋の4マス）と、その通算得点
    private readonly HashSet<Vector2Int> nexusCells = new()
    {
        new Vector2Int(8, 8), new Vector2Int(9, 8),
        new Vector2Int(8, 9), new Vector2Int(9, 9),
    };
    private readonly int[] nexusScored = new int[3];

    // センサーユニット展開中のユニット（範囲は使用者を中心に追従する）
    private readonly List<Unit> sensorUsers = new();

    // トラップ（7-2）
    private enum TrapType { Damage, Stun, Beacon }
    private class TrapInfo
    {
        public Vector2Int cell;
        public TrapType type;
        public int ownerTeam;
        public GameObject visual;
    }
    private readonly List<TrapInfo> traps = new();
    private readonly Dictionary<Unit, int> trapUses = new();
    private TrapType pendingTrapType = TrapType.Damage;

    // 特殊装備の選択待ち（使う装備の名前リスト）
    private List<string> pendingSpecialOptions = new();

    // スモークグレネード（7-2）
    private class SmokeCloud
    {
        public readonly List<Vector2Int> cells = new();
        public readonly List<GameObject> visuals = new();
        public int remainingTurnEnds;
    }
    private readonly List<SmokeCloud> smokes = new();
    private readonly Dictionary<Unit, int> smokeUses = new();

    // ドローンとハッキング（7-3）
    private enum DroneType { Damage, Sensor, Beacon, Dummy }
    private class DroneInfo
    {
        public Unit owner;       // 作成者
        public Unit controller;  // 現在の操作者（権限取得で変わる）
        public int team;         // 現在のコントロールチーム
        public DroneType type;
        public Vector2Int cell;
        public int hp = 10;
        public bool disabled;    // 機能停止中
        public GameObject visual;
        public Unit dummyTarget; // ダミーが姿を借りる味方
        public int baseAttack;   // 作成者の攻撃力（ダメージドローンの計算用）
        public int searchBonus;  // 作成者の索敵÷25（センサードローンの範囲用）
        public int bonusActions; // ドローンラッシュ：このターンの追加行動回数
        public Color baseColor;
    }
    private readonly List<DroneInfo> drones = new();
    private readonly Dictionary<DroneInfo, Vector2Int> droneOrders = new();
    private DroneInfo orderingDrone;       // 指示入力中：移動先を選んでいるドローン
    private DroneInfo currentDrone;        // 解決中：攻撃対象の選択待ちドローン
    private int droneActsLeft = 0;
    private Unit dronePauseController;     // ドローンの一時停止が終わったらスロットを終える操作者
    private Color droneOriginalColor;
    // 連携追撃（ステップ8）：このターン最初にその敵へ命中させた攻撃者と発動済みチーム
    private readonly Dictionary<Unit, Unit> firstHitBy = new();
    private readonly HashSet<int> linkageUsedTeams = new();

    private int pendingHackType = 1;       // 実行待ちのハッキングの狙い
    private DroneInfo pendingHackTarget;   // 種別選択待ちのハッキング対象ドローン
    private bool pendingUltHack = false;   // オーバーライド（確実に権限取得）かどうか
    private DroneType pendingDroneType = DroneType.Damage; // 解決時に選ばれた、作成するドローンの種類
    private Vector2Int pendingDroneCell; // ドローンの設置先（ダミーの姿指定まで持ち越す）

    // 同時入力：チームごとの準備完了フラグと制限時間タイマー
    private readonly bool[] teamReady = new bool[3];
    private float orderTimer = 0f;

    // 指示入力用：移動先を決めた後、準備行動の選択待ちか
    private bool choosingAction = false;
    // 特殊装備準備を選んで、使う装備の選択待ちか
    private bool choosingSpecial = false;
    // ドローンの種類の選択待ちか

    // 解決用
    private List<Unit> initiative = new();
    private int resolveIndex = 0;
    private Unit currentAttacker; // 一時停止中のユニット
    private PauseKind pauseKind = PauseKind.None;
    private int ultAttacksLeft = 0; // 疾風斬の残り攻撃回数
    private int pauseGrenadeRadius = 1; // グレネード一時停止中の爆風半径
    private int pauseGrenadeMult = 1;   // グレネード一時停止中の地形ダメージ倍率
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
        if (phase == Phase.Resolution && kb != null
            && kb.spaceKey.wasPressedThisFrame
            && (currentAttacker != null || currentDrone != null))
        {
            if (currentDrone != null)
            {
                Debug.Log($"{DroneName(currentDrone)} は攻撃しない");
                EndDronePause();
            }
            else if (pauseKind == PauseKind.KaedeMove)
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

        // 【解決中】ハッキングの種別選択：対象は選択済み。数字キーで種別を決めて解決
        if (phase == Phase.Resolution && pauseKind == PauseKind.HackChoice
            && currentAttacker != null && kb != null)
        {
            int pick = -1;
            if (kb.digit1Key.wasPressedThisFrame) pick = 1;
            else if (kb.digit2Key.wasPressedThisFrame) pick = 2;
            else if (kb.digit3Key.wasPressedThisFrame) pick = 3;
            if (pick > 0 && pendingHackTarget != null)
            {
                pendingHackType = pick;
                Debug.Log($"ハッキングの種別：{HackTypeLabel(pick)}");
                ResolveHack(currentAttacker, pendingHackTarget);
                pendingHackTarget = null;
                EndAttackSelection();
            }
            return;
        }

        // 【解決中】ドローン作成：種類を数字キーで選んだら、設置マスの選択へ進む
        if (phase == Phase.Resolution && pauseKind == PauseKind.DroneCreateType
            && currentAttacker != null && kb != null)
        {
            int pick = -1;
            if (kb.digit1Key.wasPressedThisFrame) pick = 0;
            else if (kb.digit2Key.wasPressedThisFrame) pick = 1;
            else if (kb.digit3Key.wasPressedThisFrame) pick = 2;
            else if (kb.digit4Key.wasPressedThisFrame) pick = 3;
            if (pick >= 0)
            {
                pendingDroneType = (DroneType)pick;
                Debug.Log($"{currentAttacker.displayName} のドローン：{DroneTypeLabel(pendingDroneType)}を作成する");
                BeginPause(currentAttacker, PauseKind.DronePlace);
                ShowDronePlaceRange(currentAttacker);
                Debug.Log("設置するマスをクリックしてください（Spaceでキャンセル）");
            }
            return;
        }

        // トラップ設置中：数字キーで種類を切り替える
        if (phase == Phase.Resolution && pauseKind == PauseKind.TrapPlace
            && kb != null)
        {
            if (kb.digit1Key.wasPressedThisFrame) SetTrapType(TrapType.Damage);
            else if (kb.digit2Key.wasPressedThisFrame) SetTrapType(TrapType.Stun);
            else if (kb.digit3Key.wasPressedThisFrame) SetTrapType(TrapType.Beacon);
        }

        // 準備行動の選択待ち（Q/W/E/Rキー）
        if (choosingAction && selected != null && kb != null)
        {
            // 特殊装備の選択待ちなら、数字キーで使う装備を決めて指示確定
            if (choosingSpecial)
            {
                int pick = -1;
                if (kb.digit1Key.wasPressedThisFrame) pick = 0;
                else if (kb.digit2Key.wasPressedThisFrame) pick = 1;
                else if (kb.digit3Key.wasPressedThisFrame) pick = 2;
                else if (kb.digit4Key.wasPressedThisFrame) pick = 3;
                else if (kb.digit5Key.wasPressedThisFrame) pick = 4;
                if (pick >= 0 && pick < pendingSpecialOptions.Count)
                {
                    FinalizeSpecialOrder(pendingSpecialOptions[pick]);
                    return;
                }
            }
            if (kb.qKey.wasPressedThisFrame) { FinalizeOrder(PreparedAction.Attack); return; }
            if (kb.wKey.wasPressedThisFrame) { FinalizeOrder(PreparedAction.Defend); return; }
            if (kb.eKey.wasPressedThisFrame) { BeginSpecialChoice(); return; }
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
        if (phase == Phase.Orders && !choosingAction && orderingDrone == null
            && kb != null
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
        if (phase == Phase.Resolution
            && (currentAttacker != null || currentDrone != null))
        {
            HandleResolutionClick(clicked, cell);
            return;
        }

        if (phase == Phase.Resolution) return;

        // 【指示入力】ドローンの移動先選択中
        if (phase == Phase.Orders && orderingDrone != null)
        {
            if (movable.Contains(cell))
            {
                droneOrders[orderingDrone] = cell;
                Debug.Log($"{DroneName(orderingDrone)} の移動先を {cell} に指示した");
                int team = orderingDrone.controller.team;
                orderingDrone = null;
                ClearHighlights();
                CheckAutoReady(team);
            }
            return; // 範囲外のクリックは無視して待ち続ける
        }

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
        // ドローンの攻撃対象の選択中
        if (currentDrone != null)
        {
            HandleDroneClick(clicked, cell);
            return;
        }

        switch (pauseKind)
        {
            case PauseKind.HackChoice:
                return; // 数字キー専用（Update側で処理）。クリックは無視

            case PauseKind.DroneCreateType:
                return; // 数字キー専用（Update側で処理）。クリックは無視

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
                DroneInfo kaedeDrone = FindDroneAt(cell);
                if (clicked == null && kaedeDrone != null
                    && kaedeDrone.team != currentAttacker.team
                    && attackable.Contains(cell)
                    && CanTargetDrone(currentAttacker, kaedeDrone))
                {
                    AttackDrone(currentAttacker, kaedeDrone);
                    ultAttacksLeft--;
                    if (!gameOver && currentAttacker.gameObject.activeSelf
                        && ultAttacksLeft > 0
                        && AnyTargetableEnemy(currentAttacker, currentAttacker.cell))
                    {
                        ShowAttackRangeFrom(currentAttacker, currentAttacker.cell,
                            new Color(1f, 0f, 0f, 0.35f));
                        Debug.Log($"疾風斬：もう1回攻撃できます（残り {ultAttacksLeft} 回）。対象を選んでください");
                        return; // 一時停止を継続
                    }
                    EndAttackSelection();
                    return;
                }
                if (clicked != null && clicked.team != currentAttacker.team
                    && attackable.Contains(cell)
                    && CanSee(currentAttacker, clicked))
                {
                    Attack(currentAttacker, clicked);
                    ultAttacksLeft--;
                    if (!gameOver
                        && currentAttacker.gameObject.activeSelf
                        && ultAttacksLeft > 0
                        && AnyTargetableEnemy(currentAttacker, currentAttacker.cell))
                    {
                        ShowAttackRangeFrom(currentAttacker, currentAttacker.cell,
                            new Color(1f, 0f, 0f, 0.35f));
                        Debug.Log($"疾風斬：もう1回攻撃できます（残り {ultAttacksLeft} 回）。対象を選んでください");
                        return; // 一時停止を継続
                    }
                }
                else if (clicked != null && clicked.team != currentAttacker.team
                         && attackable.Contains(cell))
                {
                    Debug.Log($"{clicked.displayName} は目視できていないため攻撃できない");
                }
                else
                {
                    Debug.Log($"{currentAttacker.displayName} は疾風斬の攻撃を終えた");
                }
                EndAttackSelection();
                return;

            case PauseKind.YukiSnipe:
                DroneInfo snipeDrone = FindDroneAt(cell);
                if (clicked == null && snipeDrone != null
                    && snipeDrone.team != currentAttacker.team
                    && attackable.Contains(cell)
                    && CanTargetDrone(currentAttacker, snipeDrone))
                {
                    AttackDrone(currentAttacker, snipeDrone, true, 1.5f);
                    EndAttackSelection();
                    return;
                }
                if (clicked != null && clicked.team != currentAttacker.team
                    && attackable.Contains(cell)
                    && CanSee(currentAttacker, clicked))
                {
                    Attack(currentAttacker, clicked, true, 1.5f);
                }
                else if (clicked != null && clicked.team != currentAttacker.team
                         && attackable.Contains(cell))
                {
                    Debug.Log($"{clicked.displayName} は目視できていないため撃てない（EXは消費済み）");
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

            case PauseKind.TrapPlace:
                // 自分の周囲1マスの通行可能なマスに設置
                if (movable.Contains(cell))
                {
                    PlaceTrap(currentAttacker, cell, pendingTrapType);
                }
                else
                {
                    Debug.Log("そのマスには設置できないため、トラップを設置しなかった");
                }
                EndAttackSelection();
                return;

            case PauseKind.SmokePlace:
                // 射程4以内のマスを中心に十字5マスの煙幕
                if (attackable.Contains(cell))
                {
                    PlaceSmoke(currentAttacker, cell);
                }
                else
                {
                    Debug.Log($"{currentAttacker.displayName} はスモークグレネードを使わなかった");
                }
                EndAttackSelection();
                return;

            case PauseKind.HackTarget:
                // 半径3マス以内の敵ドローンをクリックで選ぶ
                DroneInfo hackTarget = FindDroneAt(cell);
                if (hackTarget != null && hackTarget.team != currentAttacker.team
                    && Vector2.Distance(currentAttacker.cell, hackTarget.cell) <= 3f)
                {
                    if (pendingUltHack)
                    {
                        // オーバーライド：種別は権限取得で固定、そのまま解決
                        ResolveHack(currentAttacker, hackTarget);
                        pendingUltHack = false;
                        EndAttackSelection();
                        return;
                    }
                    // 対象が決まったら、次に種別を選ぶ
                    pendingHackTarget = hackTarget;
                    BeginPause(currentAttacker, PauseKind.HackChoice);
                    Debug.Log($"対象は {DroneName(hackTarget)}。ハッキングの種別を数字キーで選んでください [1]機能停止（成功率80%） [2]権限取得（50%） [3]爆破（50%）");
                    return;
                }
                Debug.Log($"{currentAttacker.displayName} はハッキング対象を選ばなかった");
                pendingUltHack = false;
                EndAttackSelection();
                return;

            case PauseKind.DronePlace:
                // ハイライトされたマスをクリックで設置先を決める
                if (movable.Contains(cell))
                {
                    if (pendingDroneType == DroneType.Dummy)
                    {
                        pendingDroneCell = cell;
                        BeginPause(currentAttacker, PauseKind.DroneDummyTarget);
                        Debug.Log($"{currentAttacker.displayName} のダミードローン：姿を借りる味方をクリックしてください");
                        return;
                    }
                    CreateDroneAt(currentAttacker, pendingDroneType, cell, null);
                }
                else
                {
                    Debug.Log($"{currentAttacker.displayName} はドローンを作成しなかった");
                }
                EndAttackSelection();
                return;

            case PauseKind.DroneDummyTarget:
                // ダミードローンが姿を借りる味方をクリックで選ぶ（設置マスは選択済み）
                if (clicked != null && clicked.team == currentAttacker.team)
                {
                    CreateDroneAt(currentAttacker, DroneType.Dummy, pendingDroneCell, clicked);
                }
                else
                {
                    Debug.Log($"{currentAttacker.displayName} はダミードローンを作成しなかった");
                }
                EndAttackSelection();
                return;

            case PauseKind.Grenade:
                // 地点指定：射程内のマスをクリックで着弾（ユニットの有無は問わない）
                if (attackable.Contains(cell))
                {
                    ResolveGrenade(currentAttacker, cell,
                        pauseGrenadeRadius, pauseGrenadeMult);
                }
                else
                {
                    Debug.Log($"{currentAttacker.displayName} はグレネードを撃たなかった");
                }
                EndAttackSelection();
                return;

            default: // PauseKind.Attack（通常攻撃の対象選択）
                DroneInfo droneHit = FindDroneAt(cell);
                if (clicked == null && droneHit != null
                    && droneHit.team != currentAttacker.team
                    && attackable.Contains(cell)
                    && CanTargetDrone(currentAttacker, droneHit))
                {
                    AttackDrone(currentAttacker, droneHit);
                    EndAttackSelection();
                    return;
                }
                if (clicked != null && clicked.team != currentAttacker.team
                    && attackable.Contains(cell)
                    && CanSee(currentAttacker, clicked))
                {
                    Attack(currentAttacker, clicked);
                }
                else if (clicked != null && clicked.team != currentAttacker.team
                         && attackable.Contains(cell))
                {
                    Debug.Log($"{clicked.displayName} は目視できていないため攻撃できない");
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
        if (phase == Phase.Orders && !choosingAction && orderingDrone == null)
            ReadyTeam(1);
    }

    public void ReadyTeam2Button()
    {
        if (phase == Phase.Orders && !choosingAction && orderingDrone == null)
            ReadyTeam(2);
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
    public void ChooseSpecial() { if (choosingAction) BeginSpecialChoice(); }
    public void ChooseUlt() { if (choosingAction) FinalizeOrder(PreparedAction.Ult); }

    // 特殊装備準備：使う装備を指示の時点で選ぶ
    void BeginSpecialChoice()
    {
        var options = BuildSpecialOptions(selected);
        if (options.Count == 0)
        {
            Debug.Log($"{selected.displayName} はいま使用できる特殊装備がないため、特殊装備準備を選べません。別の準備行動を選んでください");
            return; // 確定せず、行動選択モードのまま
        }
        if (options.Count == 1)
        {
            FinalizeSpecialOrder(options[0]);
            return;
        }
        choosingSpecial = true;
        pendingSpecialOptions = options;
        var parts = new List<string>();
        for (int i = 0; i < options.Count; i++)
            parts.Add($"[{i + 1}]{options[i]}");
        Debug.Log($"{selected.displayName} の特殊装備：使うものを数字キーで選んでください {string.Join(" ", parts)}");
    }

    // 特殊装備を決めて指示を確定する
    // （ドローンの種類と設置マス、ハッキングの種別は、いずれも解決時に選ぶ）
    void FinalizeSpecialOrder(string choice)
    {
        Order o = GetOrder(selected);
        o.specialChoice = choice;
        FinalizeOrder(PreparedAction.Special);
    }

    void FinalizeOrder(PreparedAction action)
    {
        // ULT準備は、指示時点でEXを持っている場合のみ選択できる
        if (action == PreparedAction.Ult && selected.exPoints < 1)
        {
            Debug.Log($"{selected.displayName} はEXを持っていないため、ULT準備を選べません。別の準備行動を選んでください");
            return; // 確定せず、行動選択モードのまま
        }

        choosingSpecial = false;
        pendingSpecialOptions = new List<string>();

        Unit unit = selected;
        Order o = GetOrder(unit);
        o.prepared = action;
        SetMarkerColor(o, action);
        string label = action switch
        {
            PreparedAction.Attack => "攻撃準備",
            PreparedAction.Defend => "防御準備",
            PreparedAction.Special => $"特殊装備の使用準備（{o.specialChoice}）",
            PreparedAction.Ult => "ULT準備",
            _ => "行動なし",
        };
        Debug.Log($"{unit.displayName} の指示確定：{label}");
        choosingAction = false;
        if (actionPanel != null) actionPanel.SetActive(false);
        Select(null);

        // ドローンを運用している場合は、続けてドローンの指示を受ける
        DroneInfo drone = FindDroneControlledBy(unit);
        if (drone != null && DroneUsable(drone))
        {
            orderingDrone = drone;
            ShowDroneRange(drone);
            Debug.Log($"{unit.displayName} のドローン（{DroneName(drone)}）の指示：移動先を選んでください（ドローンのマスをクリックで移動しない）");
            return;
        }

        CheckAutoReady(unit.team);
    }

    // そのチームの全ユニットの指示がそろったら自動で準備完了
    void CheckAutoReady(int team)
    {
        if (AllUnitsOrdered(team))
        {
            Debug.Log($"チーム{team} の全ユニットの指示が完了したため、自動で準備完了にします");
            ReadyTeam(team);
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
        orderingDrone = null;
        ClearHighlights();
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
                // 到着先が塞がれていたら、来た方向の手前の空きマスで止まる
                Vector2Int stop = FindStopCell(u.cell, dest, false, null);
                if (stop != u.cell)
                {
                    u.cell = stop;
                    u.SnapToCell();
                    u.movedThisTurn = true;
                    Debug.Log(stop == dest
                        ? $"{u.displayName} は {dest} へ移動"
                        : $"{u.displayName} は {stop} へ移動（到着先 {dest} が塞がれていたため手前で停止）");

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

                    // ヒールポイント：止まると回復15＋毒解除（1回使うと消滅）
                    if (terrain != null
                        && terrain.GetTerrain(u.cell) == TerrainType.Heal
                        && terrain.ConsumeHeal(u.cell))
                    {
                        int beforeHp = u.hp;
                        u.hp = Mathf.Min(u.maxHp, u.hp + 15);
                        bool curedPoison = u.poisoned;
                        u.poisoned = false;
                        if (curedPoison) u.poisonSourceTeam = 0;
                        Debug.Log($"{u.displayName} はヒールポイントで回復：HP {beforeHp} → {u.hp}{(curedPoison ? "、毒を解除" : "")}（このヒールポイントは消滅）");
                    }

                    // トラップ：敵の罠の半径1マス以内に入ったら自動発動
                    CheckTraps(u);
                }
                else
                {
                    Debug.Log($"{u.displayName} の移動は失敗（到着先が塞がれていて進めなかった）");
                }
            }

            // トラップで倒れた場合は行動できない
            if (!u.gameObject.activeSelf)
            {
                resolveIndex++;
                continue;
            }

            // トラップで行動不能になった場合は、このターンの行動を失う
            if (u.stunTurns > 0)
            {
                u.stunTurns--;
                Debug.Log($"{u.displayName} は行動不能で、このターンの行動ができない");
                resolveIndex++;
                continue;
            }

            // ② 準備行動を試みる
            PreparedAction action = o?.prepared ?? PreparedAction.None;
            if (action == PreparedAction.Defend)
            {
                Debug.Log(u.shieldBlockRate > 0
                    ? $"{u.displayName} は防御態勢（ブロック率 {u.shieldBlockRate}%）"
                    : $"{u.displayName} は防御態勢（シールドなし：ブロック不可）");
                if (ResolveDroneTurn(u)) return;
                resolveIndex++;
                continue;
            }
            if (action == PreparedAction.Special)
            {
                if (ResolveSpecialFromOrder(u, o)) return; // 一時停止（トラップ／スモークの設置先選択）
                if (ResolveDroneTurn(u)) return;
                resolveIndex++;
                continue;
            }
            if (action == PreparedAction.Ult)
            {
                if (ActivateUlt(u)) return; // 一時停止（対象選択など）
                if (ResolveDroneTurn(u)) return;
                resolveIndex++;
                continue;
            }
            if (action == PreparedAction.Attack)
            {
                if (IsGrenadeUser(u))
                {
                    // グレネードは地点指定攻撃：爆風が届く範囲に敵がいれば発射できる
                    if (AnyEnemyWithin(u, u.cell, u.weaponRange + 1))
                    {
                        pauseGrenadeRadius = 1;
                        pauseGrenadeMult = 1;
                        BeginPause(u, PauseKind.Grenade);
                        ShowAttackRangeFrom(u, u.cell,
                            new Color(1f, 0.5f, 0f, 0.35f));
                        Debug.Log($"{u.displayName} のグレネード：着弾させるマスを選んでください（撃たない場合はSpace）");
                        return;
                    }
                    Debug.Log($"{u.displayName} の攻撃準備：爆風の届く範囲に敵がおらずスキップ");
                    if (ResolveDroneTurn(u)) return;
                    resolveIndex++;
                    continue;
                }
                if (AnyTargetableEnemy(u, u.cell))
                {
                    // 対象選択のため一時停止
                    BeginPause(u, PauseKind.Attack);
                    ShowAttackRangeFrom(u, u.cell,
                        new Color(1f, 0f, 0f, 0.35f));
                    Debug.Log($"{u.displayName} の攻撃：対象を選んでください（攻撃可能な対象：{TargetableNames(u)}）");
                    return;
                }
                Debug.Log($"{u.displayName} の攻撃準備：射程内に攻撃可能な敵がおらずスキップ（目視できていない敵は攻撃できない）");
                if (ResolveDroneTurn(u)) return;
                resolveIndex++;
                continue;
            }

            if (ResolveDroneTurn(u)) return;
            resolveIndex++; // 行動なし（指示なしユニット）
        }

        if (!gameOver) FinishRound();
    }

    // 一時停止の開始（ユニットを黄色くハイライト）
    void BeginPause(Unit u, PauseKind kind)
    {
        if (currentAttacker == u)
        {
            // すでにハイライト中なら、一時停止の種類だけ切り替える
            // （特殊装備の選択 → 設置先の選択、などの連続した一時停止用）
            pauseKind = kind;
            return;
        }
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
        Unit finished = currentAttacker;
        if (currentAttacker != null)
        {
            var sr = currentAttacker.GetComponent<SpriteRenderer>();
            sr.color = attackerOriginalColor;
            currentAttacker.transform.localScale = attackerOriginalScale;
            currentAttacker = null;
        }
        pauseKind = PauseKind.None;
        ultAttacksLeft = 0;
        pendingSpecialOptions = new List<string>();
        pendingUltHack = false;
        pendingHackTarget = null;
        ClearHighlights();
        // 終わったユニットがドローンを運用していれば、続けてドローンを解決する
        if (finished != null && ResolveDroneTurn(finished)) return;
        resolveIndex++;
        if (!gameOver) AdvanceResolution();
    }

    // ドローンの一時停止が終わったときに、操作者のスロットを終える
    void CompleteSlot(Unit controller)
    {
        if (controller != null && ResolveDroneTurn(controller)) return;
        resolveIndex++;
        if (!gameOver) AdvanceResolution();
    }

    // 疾風斬：追加移動のあと、近接攻撃フェイズへ
    void StartKaedeAttacks()
    {
        ultAttacksLeft = 2;
        if (AnyTargetableEnemy(currentAttacker, currentAttacker.cell))
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
                if (!AnyTargetableEnemy(u, u.cell))
                {
                    Debug.Log($"{u.displayName} のULT「{ultName}」：射程内に敵がおらず不発（EXは消費しない）");
                    return false;
                }
                u.exPoints--;
                Debug.Log($"{u.displayName} のULT「{ultName}」発動！（EX {u.exPoints}/{Unit.MaxEx}）必中・ダメージ+50%の射撃");
                BeginPause(u, PauseKind.YukiSnipe);
                ShowAttackRangeFrom(u, u.cell,
                    new Color(1f, 0f, 0f, 0.35f));
                Debug.Log($"狙撃する対象を選んでください（攻撃可能な対象：{TargetableNames(u)}）");
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

            case "レン": // 破砕砲撃：そのターンのグレネードが半径2＋地形ダメージ2倍
                if (!IsGrenadeUser(u))
                {
                    Debug.Log($"{u.displayName} のULT「{ultName}」：グレネードランチャーを装備していないため不発（EXは消費しない）");
                    return false;
                }
                if (!AnyEnemyWithin(u, u.cell, u.weaponRange + 2))
                {
                    Debug.Log($"{u.displayName} のULT「{ultName}」：爆風の届く範囲に敵がおらず不発（EXは消費しない）");
                    return false;
                }
                u.exPoints--;
                Debug.Log($"{u.displayName} のULT「{ultName}」発動！（EX {u.exPoints}/{Unit.MaxEx}）グレネードの半径2＋地形ダメージ2倍");
                pauseGrenadeRadius = 2;
                pauseGrenadeMult = 2;
                BeginPause(u, PauseKind.Grenade);
                ShowAttackRangeFrom(u, u.cell,
                    new Color(1f, 0.5f, 0f, 0.35f));
                Debug.Log("着弾させるマスを選んでください");
                return true;

            case "ノイ": // ドローンラッシュ：即作成、または既存ドローンが2回行動
                DroneInfo own = FindDroneControlledBy(u);
                if (own != null && DroneUsable(own))
                {
                    u.exPoints--;
                    own.bonusActions += 1;
                    Debug.Log($"{u.displayName} のULT「{ultName}」発動！（EX {u.exPoints}/{Unit.MaxEx}）このターン、ドローンが2回行動できる");
                    return false;
                }
                DroneInfo created = CreateDrone(u, DroneType.Damage, null);
                if (created == null)
                {
                    Debug.Log($"{u.displayName} のULT「{ultName}」：隣接マスに空きがなくドローンを作成できなかった（EXは消費しない）");
                    return false;
                }
                u.exPoints--;
                Debug.Log($"{u.displayName} のULT「{ultName}」発動！（EX {u.exPoints}/{Unit.MaxEx}）ダメージドローンを即座に作成した（このターンは指示がないため待機）");
                return false;

            case "ミコト": // オーバーライド：敵ドローン・設置物1つを確実に権限取得
                if (!AnyEnemyDroneWithin(u.team, u.cell, 3))
                {
                    Debug.Log($"{u.displayName} のULT「{ultName}」：半径3マス以内に敵ドローンがおらず不発（EXは消費しない）");
                    return false;
                }
                u.exPoints--;
                pendingUltHack = true;
                Debug.Log($"{u.displayName} のULT「{ultName}」発動！（EX {u.exPoints}/{Unit.MaxEx}）権限取得する敵ドローンを選んでください");
                BeginPause(u, PauseKind.HackTarget);
                return true;

            default: // 全キャラのULTが実装済み（ここに来るのは想定外）
                Debug.Log($"{u.displayName} のULT「{ultName}」の効果は未実装（EXは消費しない）");
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

    // そのユニットが、いま使用できる特殊装備の一覧（ビーコン時は切除も含む）
    List<string> BuildSpecialOptions(Unit u)
    {
        var options = new List<string>();
        if (u.beaconed) options.Add("切除");
        if (u.data != null && u.data.equipment != null)
        {
            foreach (var item in u.data.equipment)
            {
                if (item == null || item.kind != ItemKind.Special) continue;
                if (options.Contains(item.itemName)) continue;
                if (!IsSpecialUsable(u, item.itemName)) continue;
                options.Add(item.itemName);
            }
        }
        return options;
    }

    // 解決時：指示の時点で選んだ特殊装備を実行する（一時停止したら true）
    bool ResolveSpecialFromOrder(Unit u, Order o)
    {
        string choice = o?.specialChoice;
        if (choice == null)
        {
            // 指示時点で選ばれていない場合（通常は起きない）
            var options = BuildSpecialOptions(u);
            if (options.Count == 0)
            {
                Debug.Log($"{u.displayName} の特殊装備使用：いま使用できる特殊装備がない");
                return false;
            }
            choice = options[0];
        }
        return ExecuteSpecial(u, choice);
    }

    // その特殊装備が、いま使える状態かどうか
    bool IsSpecialUsable(Unit u, string itemName)
    {
        switch (itemName)
        {
            case "光学迷彩": return !u.camouflaged; // 発動中は選べない
            case "センサーユニット": return !sensorUsers.Contains(u); // 展開中は選べない
            case "トラップユニット": return TrapUsesLeft(u) > 0;
            case "スモークグレネード": return SmokeUsesLeft(u) > 0;
            case "ドローンユニット": return FindDroneControlledBy(u) == null; // 運用中は作成できない
            case "ハッキングユニット":
                // 移動先（未定なら現在地）から半径3マス以内に敵ドローンがいる場合のみ
                var o = GetOrder(u);
                Vector2Int from = o.moveCell ?? u.cell;
                return AnyEnemyDroneWithin(u.team, from, 3);
            default: return true; // メディカルキット等（効果は未実装）
        }
    }

    // 選ばれた特殊装備（または切除）を実行する。設置先の選択で一時停止したら true
    bool ExecuteSpecial(Unit u, string choice)
    {
        switch (choice)
        {
            case "切除": // ビーコン切除：自傷5ダメージでビーコンを外す
                if (!u.beaconed)
                {
                    Debug.Log($"{u.displayName} の切除：ビーコンはすでに外れているため、何もしなかった");
                    return false;
                }
                u.beaconed = false;
                u.hp -= 5;
                Debug.Log($"{u.displayName} はビーコンを切除した（自傷5ダメージ、残りHP {Mathf.Max(u.hp, 0)}）");
                if (u.hp <= 0)
                {
                    Debug.Log($"{u.displayName} は切除のダメージで倒れた（この撃破では得点は入らない）");
                    u.gameObject.SetActive(false);
                    CheckWin();
                }
                return false;

            case "光学迷彩": // 有効化（攻撃する・被弾すると解除される）
                if (u.camouflaged)
                {
                    Debug.Log($"{u.displayName} の光学迷彩：すでに有効化されているため、何もしなかった");
                    return false;
                }
                u.camouflaged = true;
                Debug.Log($"{u.displayName} は光学迷彩を有効化した（敵から目視不可になる。攻撃・被弾で解除）");
                return false;

            case "センサーユニット": // 使用者を中心とするセンサー範囲を展開
                if (sensorUsers.Contains(u))
                {
                    Debug.Log($"{u.displayName} のセンサーユニット：すでに展開されているため、何もしなかった");
                    return false;
                }
                sensorUsers.Add(u);
                Debug.Log($"{u.displayName} はセンサーユニットを展開した（半径 {4 + u.search / 25} マス以内の敵を把握し、迷彩・ブッシュも看破する）");
                return false;

            case "トラップユニット": // 設置先と種類を選んで設置（1試合3回まで）
                if (TrapUsesLeft(u) <= 0)
                {
                    Debug.Log($"{u.displayName} のトラップユニット：使用回数を使い切っているため、何もしなかった");
                    return false;
                }
                pendingTrapType = TrapType.Damage;
                BeginPause(u, PauseKind.TrapPlace);
                ShowTrapRange(u);
                Debug.Log($"{u.displayName} のトラップ設置：設置するマスを選んでください（種類はキーで切替 [1]ダメージ [2]行動不能 [3]ビーコン、現在：ダメージ／残り {TrapUsesLeft(u)} 回）");
                return true;

            case "スモークグレネード": // 中心マスを選んで十字5マスの煙幕（1試合3回まで）
                if (SmokeUsesLeft(u) <= 0)
                {
                    Debug.Log($"{u.displayName} のスモークグレネード：使用回数を使い切っているため、何もしなかった");
                    return false;
                }
                BeginPause(u, PauseKind.SmokePlace);
                ShowRadiusFrom(u.cell, 4, new Color(0.55f, 0.55f, 0.6f, 0.35f));
                Debug.Log($"{u.displayName} のスモークグレネード：煙幕の中心マスを選んでください（射程4、残り {SmokeUsesLeft(u)} 回）");
                return true;

            case "ドローンユニット": // 作成に1ターン。種類と設置マスは解決時に選ぶ
                if (FindDroneControlledBy(u) != null)
                {
                    Debug.Log($"{u.displayName} のドローン：すでにドローンを運用中のため作成できない");
                    return false;
                }
                if (DronePlaceCells(u).Count == 0)
                {
                    Debug.Log($"{u.displayName} のドローン：周囲に設置できるマスがなく、作成できなかった");
                    return false;
                }
                BeginPause(u, PauseKind.DroneCreateType);
                Debug.Log($"{u.displayName} のドローン：作成する種類を数字キーで選んでください [1]ダメージ [2]センサー [3]ビーコン弾 [4]ダミー（Spaceでキャンセル）");
                return true;

            case "ハッキングユニット": // 半径3マス以内の敵ドローンを狙う
                if (!AnyEnemyDroneWithin(u.team, u.cell, 3))
                {
                    Debug.Log($"{u.displayName} のハッキング：範囲内に敵ドローンがおらず空振り");
                    return false;
                }
                pendingUltHack = false;
                BeginPause(u, PauseKind.HackTarget);
                Debug.Log($"{u.displayName} のハッキング：対象のドローンを選んでください（選んだ後に種別を決めます）");
                return true;

            default:
                Debug.Log($"{u.displayName} の特殊装備使用：{choice} ※この装備の効果は未実装");
                return false;
        }
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
        // ターン終了処理：スモークの持続ターン消費（2回のターン終了で晴れる）
        for (int i = smokes.Count - 1; i >= 0; i--)
        {
            smokes[i].remainingTurnEnds--;
            if (smokes[i].remainingTurnEnds <= 0)
            {
                foreach (var v in smokes[i].visuals) Destroy(v);
                smokes.RemoveAt(i);
                Debug.Log("スモークが晴れた");
            }
        }

        // ターン終了処理：毒ダメージ
        ApplyPoisonDamage();
        if (gameOver) { RefreshReadyUI(); return; }

        // ターン終了処理：ネクサス得点
        ApplyNexusScoring();

        orders.Clear();
        droneOrders.Clear();
        firstHitBy.Clear();
        linkageUsedTeams.Clear();

        // ドローン：操作者を失ったものは機能停止する（権限取得で再起動できる）
        foreach (var d in drones)
        {
            d.bonusActions = 0;
            if (!d.disabled
                && (d.controller == null || !d.controller.gameObject.activeSelf))
            {
                d.disabled = true;
                SetDroneVisualColor(d);
                Debug.Log($"{DroneName(d)} は操作者を失い、機能停止した");
            }
        }

        teamReady[1] = false;
        teamReady[2] = false;
        orderTimer = orderTimeLimit;
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            u.movedThisTurn = false;
            u.defending = false;
            u.ultGuarding = false;
            u.exposed = false;
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

    // ターン終了処理：ネクサスマスに止まっている陣営に1点（1ターン1点まで、1試合3点まで）
    void ApplyNexusScoring()
    {
        for (int team = 1; team <= 2; team++)
        {
            if (nexusScored[team] >= 3) continue;
            foreach (Unit u in FindObjectsByType<Unit>())
            {
                if (u.team != team) continue;
                if (!nexusCells.Contains(u.cell)) continue;
                score[team] += 1;
                nexusScored[team]++;
                Debug.Log($"{u.displayName} がネクサスを保持：チーム{team} +1点（ネクサス通算 {nexusScored[team]}/3、スコア {score[1]} - {score[2]}）");
                break;
            }
        }
    }

    bool AnyEnemyInRange(Unit unit, Vector2Int origin)
    {
        return AnyEnemyWithin(unit, origin, unit.weaponRange);
    }

    bool AnyEnemyWithin(Unit unit, Vector2Int origin, float distance)
    {
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u.team == unit.team) continue;
            if (Vector2.Distance(origin, u.cell) <= distance)
                return true;
        }
        return false;
    }

    // ---- ドローンとハッキング（7-3） ----

    string DroneName(DroneInfo d)
    {
        if (d.type == DroneType.Dummy && d.dummyTarget != null)
            return d.dummyTarget.displayName;
        return DroneTypeLabel(d.type);
    }

    string DroneTypeLabel(DroneType t)
    {
        switch (t)
        {
            case DroneType.Damage: return "ダメージドローン";
            case DroneType.Sensor: return "センサードローン";
            case DroneType.Beacon: return "ビーコン弾ドローン";
            default: return "ダミードローン";
        }
    }

    string HackTypeLabel(int hackType)
    {
        switch (hackType)
        {
            case 1: return "機能停止（成功率80%）";
            case 2: return "権限取得（成功率50%）";
            default: return "爆破（成功率50%）";
        }
    }

    DroneInfo FindDroneAt(Vector2Int cell)
    {
        foreach (var d in drones)
            if (d.cell == cell) return d;
        return null;
    }

    DroneInfo FindDroneControlledBy(Unit u)
    {
        foreach (var d in drones)
            if (d.controller == u) return d;
        return null;
    }

    // ドローンが行動できる状態か（機能停止でなく、操作者が生きている）
    bool DroneUsable(DroneInfo d)
    {
        return d != null && !d.disabled
            && d.controller != null && d.controller.gameObject.activeSelf;
    }

    bool AnyEnemyDroneWithin(int team, Vector2Int origin, float dist)
    {
        foreach (var d in drones)
        {
            if (d.team == team) continue;
            if (Vector2.Distance(origin, d.cell) <= dist) return true;
        }
        return false;
    }

    void SetDroneVisualColor(DroneInfo d)
    {
        if (d.visual == null) return;
        var sr = d.visual.GetComponent<SpriteRenderer>();
        sr.color = d.disabled
            ? new Color(0.4f, 0.4f, 0.4f, 0.6f) : d.baseColor;
    }

    // ドローンを作成する（操作者の隣接マスに出現。置けなければ null）
    // ドローンを設置できるマス（作成者の周囲8マスで、通行可能・ユニット・ドローンなし）
    List<Vector2Int> DronePlaceCells(Unit controller)
    {
        var cells = new List<Vector2Int>();
        var dirs = new Vector2Int[]
        {
            new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
            new(1, 1), new(1, -1), new(-1, 1), new(-1, -1)
        };
        foreach (var dir in dirs)
        {
            Vector2Int c = controller.cell + dir;
            if (!Inside(c)) continue;
            if (terrain != null && !terrain.IsPassable(c)) continue;
            if (FindUnitAt(c) != null) continue;
            if (FindDroneAt(c) != null) continue;
            cells.Add(c);
        }
        return cells;
    }

    // 自動配置（ULTの即作成用）：空きマスのうち最初に見つかったマス
    Vector2Int? FindDroneSpawnCell(Unit controller)
    {
        var cells = DronePlaceCells(controller);
        return cells.Count > 0 ? cells[0] : null;
    }

    // 設置マスの選択肢を表示する
    void ShowDronePlaceRange(Unit unit)
    {
        ClearHighlights();
        foreach (var c in DronePlaceCells(unit))
        {
            movable.Add(c);
            AddHighlight(c, new Color(0.4f, 0.85f, 1f, 0.45f));
        }
    }

    DroneInfo CreateDrone(Unit controller, DroneType type, Unit dummyTarget)
    {
        Vector2Int? spawn = FindDroneSpawnCell(controller);
        if (spawn == null) return null;
        return CreateDroneAt(controller, type, spawn.Value, dummyTarget);
    }

    DroneInfo CreateDroneAt(Unit controller, DroneType type, Vector2Int spawn, Unit dummyTarget)
    {
        var visual = Instantiate(highlightPrefab);
        visual.transform.position
            = new Vector3(spawn.x + 0.5f, spawn.y + 0.5f, 0);
        visual.transform.localScale = Vector3.one * 0.7f;
        visual.GetComponent<SpriteRenderer>().sortingOrder = 1;

        var d = new DroneInfo
        {
            owner = controller,
            controller = controller,
            team = controller.team,
            type = type,
            cell = spawn,
            hp = 10,
            visual = visual,
            dummyTarget = dummyTarget,
            baseAttack = controller.attack,
            searchBonus = controller.search / 25
        };
        d.baseColor = type == DroneType.Dummy && dummyTarget != null
            ? dummyTarget.GetComponent<SpriteRenderer>().color
            : controller.team == 1
                ? new Color(0.35f, 0.65f, 1f, 0.9f)
                : new Color(1f, 0.5f, 0.35f, 0.9f);
        SetDroneVisualColor(d);
        drones.Add(d);
        Debug.Log($"{controller.displayName} は{DroneTypeLabel(type)}を作成した（{spawn} に出現、HP10）");
        return d;
    }

    void DestroyDrone(DroneInfo d)
    {
        drones.Remove(d);
        droneOrders.Remove(d);
        if (d.visual != null) Destroy(d.visual);
        Debug.Log($"{DroneName(d)} は撃破された（ドローンの撃破ではポイントは入らない）");
    }

    void DamageDrone(DroneInfo d, int amount)
    {
        d.hp -= amount;
        Debug.Log($"{DroneName(d)} に {amount} ダメージ（残りHP {Mathf.Max(d.hp, 0)}）");
        if (d.hp <= 0) DestroyDrone(d);
    }

    // ドローンの移動範囲を表示（4マス、ユニットや他のドローンのいるマスは除く）
    void ShowDroneRange(DroneInfo d)
    {
        ClearHighlights();
        movable.Clear();
        for (int dx = -4; dx <= 4; dx++)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                Vector2Int c = d.cell + new Vector2Int(dx, dy);
                if (!Inside(c)) continue;
                if (Vector2.Distance(d.cell, c) > 4f) continue;
                if (c != d.cell)
                {
                    if (terrain != null && !terrain.IsPassable(c)) continue;
                    if (FindUnitAt(c) != null) continue;
                    if (FindDroneAt(c) != null) continue;
                }
                movable.Add(c);
                AddHighlight(c, new Color(0.4f, 0.8f, 1f, 0.3f));
            }
        }
    }

    // ユニットからドローンを狙えるか（射線が通り、スモークの中にいない）
    bool CanTargetDrone(Unit attacker, DroneInfo d)
    {
        if (IsSmokeCell(d.cell)) return false;
        return HasLineOfSight(attacker.cell, d.cell);
    }

    // 操作者のスロット終了後に、そのドローンを解決する（一時停止したら true）
    bool ResolveDroneTurn(Unit controller)
    {
        DroneInfo d = FindDroneControlledBy(controller);
        if (d == null) return false;
        if (!DroneUsable(d)) return false;
        if (!droneOrders.TryGetValue(d, out var dest)) return false;
        droneOrders.Remove(d);

        // 移動（4マスまで。到着先が塞がれていたら手前の空きマスで止まる）
        if (dest != d.cell)
        {
            Vector2Int stop = FindStopCell(d.cell, dest, true, d);
            if (stop == d.cell)
            {
                Debug.Log($"{DroneName(d)} の移動は失敗（到着先が塞がれていて進めなかった）");
            }
            else
            {
                d.cell = stop;
                d.visual.transform.position
                    = new Vector3(stop.x + 0.5f, stop.y + 0.5f, 0);
                Debug.Log(stop == dest
                    ? $"{DroneName(d)} は {dest} へ移動"
                    : $"{DroneName(d)} は {stop} へ移動（到着先 {dest} が塞がれていたため手前で停止）");
            }
        }

        int acts = 1 + d.bonusActions;
        d.bonusActions = 0;

        switch (d.type)
        {
            case DroneType.Damage:
                if (!AnyDroneTarget(d))
                {
                    Debug.Log($"{DroneName(d)}：半径2マス内に攻撃できる敵がいない");
                    return false;
                }
                droneActsLeft = acts;
                BeginDronePause(d, controller);
                Debug.Log($"{DroneName(d)} の攻撃：対象を選んでください（命中率75%、威力10）");
                return true;

            case DroneType.Sensor:
                var seen = new List<string>();
                foreach (Unit e in FindObjectsByType<Unit>())
                {
                    if (e.team == d.team) continue;
                    if (Vector2.Distance(d.cell, e.cell) <= 3 + d.searchBonus)
                        seen.Add($"{e.displayName}（HP {e.hp}）at {e.cell}");
                }
                Debug.Log($"{DroneName(d)} のセンサー：{(seen.Count > 0 ? string.Join("、", seen) : "範囲内に敵はいない")}");
                return false;

            case DroneType.Beacon:
                int count = 0;
                foreach (Unit e in FindObjectsByType<Unit>())
                {
                    if (e.team == d.team) continue;
                    if (Vector2.Distance(d.cell, e.cell) <= 3f && !e.beaconed)
                    {
                        e.beaconed = true;
                        count++;
                        Debug.Log($"{e.displayName} はドローンによってビーコン状態になった");
                    }
                }
                if (count == 0)
                    Debug.Log($"{DroneName(d)}：半径3マス内にビーコンを付ける敵がいない");
                return false;

            default: // Dummy は行動なし
                Debug.Log($"{DroneName(d)} は待機している");
                return false;
        }
    }

    // ドローン側から狙える敵（半径2マス）の有無と判定
    bool AnyDroneTarget(DroneInfo d)
    {
        foreach (Unit e in FindObjectsByType<Unit>())
        {
            if (e.team == d.team) continue;
            if (Vector2.Distance(d.cell, e.cell) > 2f) continue;
            if (CanDroneTarget(d, e)) return true;
        }
        return false;
    }

    bool CanDroneTarget(DroneInfo d, Unit target)
    {
        if (target.beaconed) return true;
        if (IsSensedBy(d.team, target.cell)) return true;
        if (IsSmokeCell(target.cell)) return false;
        if (target.camouflaged) return false;
        if (terrain != null
            && terrain.GetTerrain(target.cell) == TerrainType.Bush
            && !target.exposed
            && Vector2.Distance(d.cell, target.cell) > 1.5f) return false;
        return HasLineOfSight(d.cell, target.cell);
    }

    void BeginDronePause(DroneInfo d, Unit controller)
    {
        currentDrone = d;
        dronePauseController = controller;
        pauseKind = PauseKind.DroneAttack;
        var sr = d.visual.GetComponent<SpriteRenderer>();
        droneOriginalColor = sr.color;
        sr.color = Color.yellow;
    }

    void EndDronePause()
    {
        if (currentDrone != null)
        {
            if (drones.Contains(currentDrone))
            {
                var sr = currentDrone.visual.GetComponent<SpriteRenderer>();
                sr.color = droneOriginalColor;
            }
            currentDrone = null;
        }
        pauseKind = PauseKind.None;
        droneActsLeft = 0;
        ClearHighlights();
        Unit ctrl = dronePauseController;
        dronePauseController = null;
        if (ctrl != null) CompleteSlot(ctrl);
    }

    // ドローンの攻撃対象のクリック処理
    void HandleDroneClick(Unit clicked, Vector2Int cell)
    {
        DroneInfo d = currentDrone;
        if (clicked != null && clicked.team != d.team
            && Vector2.Distance(d.cell, clicked.cell) <= 2f
            && CanDroneTarget(d, clicked))
        {
            ResolveDroneAttack(d, clicked);
            droneActsLeft--;
            if (!drones.Contains(d)) { EndDronePause(); return; } // 反射で自滅など
            if (!gameOver && droneActsLeft > 0 && AnyDroneTarget(d))
            {
                Debug.Log($"ドローンはもう1回攻撃できる（残り {droneActsLeft} 回）。対象を選んでください");
                return;
            }
        }
        else
        {
            Debug.Log($"{DroneName(d)} は攻撃しない");
        }
        EndDronePause();
    }

    // ドローンの攻撃（命中率75%固定、威力10、作成者の攻撃力で計算）
    void ResolveDroneAttack(DroneInfo d, Unit target)
    {
        // 鉄壁：半径2マス以内の味方への攻撃をゴウが引き受ける
        Unit guard = FindGuardFor(target);
        if (guard != null)
        {
            Debug.Log($"鉄壁：{guard.displayName} がドローンの攻撃を {target.displayName} の代わりに受ける");
            target = guard;
        }

        int roll = Random.Range(1, 101);
        if (roll > 75)
        {
            Debug.Log($"{DroneName(d)} の攻撃！ 命中率 75% → 出目 {roll} で 外れ");
            return;
        }

        float raw = (d.baseAttack + 10) / 2f
                    * 100f / (100 + target.defense + target.armorBonus);
        int damage = Mathf.Max(1, Mathf.RoundToInt(raw));

        if (target.ultGuarding)
        {
            damage = Mathf.Max(1, damage / 2);
            Debug.Log($"鉄壁：{target.displayName} の受けるダメージを半減 → {damage}");
        }

        // シールドブロック判定（ドローンの攻撃はブロック可能）
        if (target.defending && target.shieldBlockRate > 0)
        {
            int blockRoll = Random.Range(1, 101);
            if (blockRoll <= target.shieldBlockRate)
            {
                Debug.Log($"{target.displayName} のシールドブロック成功（率 {target.shieldBlockRate}%、出目 {blockRoll}）ダメージ無効");
                if (target.hasCounterShield)
                {
                    int reflect = Mathf.RoundToInt(damage * 0.5f); // 遠隔扱い50%
                    if (reflect > 0)
                    {
                        Debug.Log($"カウンターシールド：ドローンに {reflect} ダメージを反射");
                        DamageDrone(d, reflect);
                    }
                }
                return;
            }
            Debug.Log($"{target.displayName} のシールドブロック失敗（率 {target.shieldBlockRate}%、出目 {blockRoll}）");
        }

        target.hp -= damage;
        Debug.Log($"{DroneName(d)} の攻撃が命中！ 命中率 75% → 出目 {roll}、{target.displayName} に {damage} ダメージ（残りHP {Mathf.Max(target.hp, 0)}）");

        if (target.camouflaged)
        {
            target.camouflaged = false;
            Debug.Log($"{target.displayName} は被弾したため光学迷彩が解除された");
        }

        if (target.hp <= 0)
        {
            target.gameObject.SetActive(false);
            score[OtherTeam(target.team)] += 2;
            Debug.Log($"{target.displayName} は撃破された。チーム{OtherTeam(target.team)} +2点（スコア {score[1]} - {score[2]}）");
            CheckWin();
        }
    }

    // ユニットからドローンへの攻撃（ドローンの防御・敏捷性は0として計算）
    void AttackDrone(Unit attacker, DroneInfo d,
                     bool guaranteedHit = false, float damageMultiplier = 1f)
    {
        RevealAttacker(attacker);
        int hitRate = Mathf.Clamp(Mathf.RoundToInt(
            attacker.weaponAccuracy + attacker.precision / 2f
            + (attacker.movedThisTurn ? 0 : 10)), 5, 95);
        int roll = Random.Range(1, 101);
        bool hit = guaranteedHit || roll <= hitRate;
        if (!hit)
        {
            Debug.Log($"{attacker.displayName} はドローンを攻撃！ 命中率 {hitRate}% → 出目 {roll} で 外れ");
        }
        else
        {
            float raw = (attacker.attack + attacker.weaponPower) / 2f;
            if (!attacker.movedThisTurn) raw *= 1.1f;
            raw *= damageMultiplier;
            int dmg = Mathf.Max(1, Mathf.RoundToInt(raw));
            Debug.Log($"{attacker.displayName} のドローンへの攻撃が命中！（出目 {roll}）");
            DamageDrone(d, dmg);
        }
        attacker.hasAttacked = true;
    }

    // ハッキングの解決（狙いは指示時点で選択済み。ULTの場合は確実に権限取得）
    void ResolveHack(Unit hacker, DroneInfo d)
    {
        if (pendingUltHack)
        {
            SeizeDrone(hacker, d, "オーバーライド");
            return;
        }
        int roll = Random.Range(1, 101);
        switch (pendingHackType)
        {
            case 1: // 機能停止 80%
                if (roll <= 80)
                {
                    d.disabled = true;
                    SetDroneVisualColor(d);
                    Debug.Log($"ハッキング成功（出目 {roll}）：{DroneName(d)} は機能停止した");
                }
                else Debug.Log($"ハッキング失敗（出目 {roll}）：機能停止は成功率80%");
                break;

            case 2: // 権限取得 50%
                if (roll <= 50) SeizeDrone(hacker, d, "ハッキング");
                else Debug.Log($"ハッキング失敗（出目 {roll}）：権限取得は成功率50%");
                break;

            default: // 爆破 50%
                if (roll <= 50)
                {
                    Debug.Log($"ハッキング成功（出目 {roll}）：{DroneName(d)} が爆発した！");
                    ExplodeDrone(d);
                }
                else Debug.Log($"ハッキング失敗（出目 {roll}）：爆破は成功率50%");
                break;
        }
    }

    void SeizeDrone(Unit hacker, DroneInfo d, string how)
    {
        // 操作者は同時に1体まで：すでに別のドローンを操作中なら、そちらは機能停止する
        DroneInfo prev = FindDroneControlledBy(hacker);
        if (prev != null && prev != d)
        {
            prev.disabled = true;
            SetDroneVisualColor(prev);
            Debug.Log($"操作の上限（1体まで）のため、{DroneName(prev)} は機能停止した");
        }
        d.controller = hacker;
        d.team = hacker.team;
        d.disabled = false;
        if (d.type != DroneType.Dummy)
        {
            d.baseColor = hacker.team == 1
                ? new Color(0.35f, 0.65f, 1f, 0.9f)
                : new Color(1f, 0.5f, 0.35f, 0.9f);
        }
        droneOrders.Remove(d);
        SetDroneVisualColor(d);
        Debug.Log($"{how}成功：{DroneName(d)} の権限を取得した（チーム{hacker.team} のドローンとして使える）");
    }

    // 爆破：ドローンのマスの周囲1マス全員に20ダメージ（固定、味方にも当たる）
    void ExplodeDrone(DroneInfo d)
    {
        Vector2Int center = d.cell;
        DestroyDrone(d);
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (Vector2.Distance(center, u.cell) > 1f) continue;
            u.hp -= 20;
            Debug.Log($"{u.displayName} は爆発で20ダメージ（固定、残りHP {Mathf.Max(u.hp, 0)}）");
            if (u.camouflaged)
            {
                u.camouflaged = false;
                Debug.Log($"{u.displayName} は被弾したため光学迷彩が解除された");
            }
            if (u.hp <= 0)
            {
                u.gameObject.SetActive(false);
                score[OtherTeam(u.team)] += 2;
                Debug.Log($"{u.displayName} は撃破された。チーム{OtherTeam(u.team)} +2点（スコア {score[1]} - {score[2]}）");
            }
        }
        CheckWin();
    }

    // 視線チェック：壁だけが視線を遮る（水場・ブッシュは遮らない）
    bool HasLineOfSight(Vector2Int from, Vector2Int to)
    {
        if (terrain == null) return true;
        var a = new Vector2(from.x + 0.5f, from.y + 0.5f);
        var b = new Vector2(to.x + 0.5f, to.y + 0.5f);
        int steps = Mathf.CeilToInt(Vector2.Distance(a, b) * 4f);
        for (int i = 1; i < steps; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, (float)i / steps);
            var cell = new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
            if (cell == from || cell == to) continue;
            if (terrain.GetTerrain(cell) == TerrainType.Wall) return false;
            if (IsSmokeCell(cell)) return false; // 煙幕も視線を遮る（壁と同じ扱い）
        }
        return true;
    }

    // 指定チームのセンサー範囲内にそのマスが入っているか
    bool IsSensedBy(int team, Vector2Int cell)
    {
        foreach (Unit s in sensorUsers)
        {
            if (s.team != team || !s.gameObject.activeSelf) continue;
            int radius = 4 + s.search / 25;
            if (Vector2.Distance(s.cell, cell) <= radius) return true;
        }
        // センサードローンの範囲（半径3＋作成者の索敵ボーナス）
        foreach (DroneInfo d in drones)
        {
            if (d.type != DroneType.Sensor || d.team != team) continue;
            if (!DroneUsable(d)) continue;
            if (Vector2.Distance(d.cell, cell) <= 3 + d.searchBonus) return true;
        }
        return false;
    }

    // viewer が target を目視できているか（情報戦の基本判定）
    bool CanSee(Unit viewer, Unit target)
    {
        if (target == null || !target.gameObject.activeSelf) return false;
        if (viewer == target || viewer.team == target.team) return true;
        if (target.beaconed) return true; // 位置を把握：壁抜き・迷彩も貫通
        if (IsSensedBy(viewer.team, target.cell)) return true; // センサーは迷彩・ブッシュを看破
        if (IsSmokeCell(target.cell)) return false; // 煙幕の中のユニットは目視不可

        float dist = Vector2.Distance(viewer.cell, target.cell);
        if (dist > viewer.VisionRange) return false;
        if (target.camouflaged && viewer.search < 80) return false; // 迷彩は索敵80以上が看破
        if (terrain != null
            && terrain.GetTerrain(target.cell) == TerrainType.Bush
            && !target.exposed && dist > 1.5f) return false; // ブッシュは隣接しないと見えない
        return HasLineOfSight(viewer.cell, target.cell);
    }

    // 射程内にいて、かつ目視できている敵がいるか（攻撃対象にできる敵）
    bool AnyTargetableEnemy(Unit unit, Vector2Int origin)
    {
        foreach (Unit e in FindObjectsByType<Unit>())
        {
            if (e.team == unit.team) continue;
            if (Vector2.Distance(origin, e.cell) > unit.weaponRange) continue;
            if (CanSee(unit, e)) return true;
        }
        foreach (DroneInfo d in drones)
        {
            if (d.team == unit.team) continue;
            if (Vector2.Distance(origin, d.cell) > unit.weaponRange) continue;
            if (CanTargetDrone(unit, d)) return true;
        }
        return false;
    }

    // 攻撃可能な対象の名前一覧（対象選択時のログ用）
    string TargetableNames(Unit unit)
    {
        var names = new List<string>();
        foreach (Unit e in FindObjectsByType<Unit>())
        {
            if (e.team == unit.team) continue;
            if (Vector2.Distance(unit.cell, e.cell) > unit.weaponRange) continue;
            if (CanSee(unit, e)) names.Add(e.displayName);
        }
        foreach (DroneInfo d in drones)
        {
            if (d.team == unit.team) continue;
            if (Vector2.Distance(unit.cell, d.cell) > unit.weaponRange) continue;
            if (CanTargetDrone(unit, d)) names.Add(DroneName(d));
        }
        return names.Count > 0 ? string.Join("、", names) : "なし";
    }

    // グレネードランチャーを装備しているか
    bool IsGrenadeUser(Unit unit)
        => unit.weapon != null && unit.weapon.name == "W_Grenade";

    // ---- トラップとスモーク（7-2） ----

    int TrapUsesLeft(Unit u)
        => 3 - (trapUses.TryGetValue(u, out int n) ? n : 0);

    int SmokeUsesLeft(Unit u)
        => 3 - (smokeUses.TryGetValue(u, out int n) ? n : 0);

    static string TrapLabel(TrapType type) => type switch
    {
        TrapType.Damage => "ダメージ",
        TrapType.Stun => "行動不能",
        _ => "ビーコン",
    };

    // トラップ設置中：数字キーで種類を切り替える
    void SetTrapType(TrapType type)
    {
        pendingTrapType = type;
        Debug.Log($"トラップの種類：{TrapLabel(type)}（設置するマスをクリック）");
    }

    void PlaceTrap(Unit u, Vector2Int cell, TrapType type)
    {
        trapUses[u] = (trapUses.TryGetValue(u, out int n) ? n : 0) + 1;
        var visual = Instantiate(highlightPrefab);
        visual.transform.position = new Vector3(cell.x + 0.5f, cell.y + 0.5f, 0);
        visual.transform.localScale = Vector3.one * 0.55f;
        var sr = visual.GetComponent<SpriteRenderer>();
        sr.sortingOrder = 1;
        sr.color = type switch
        {
            TrapType.Damage => new Color(0.9f, 0.25f, 0.15f, 0.85f),
            TrapType.Stun => new Color(0.6f, 0.3f, 0.9f, 0.85f),
            _ => new Color(0.95f, 0.8f, 0.2f, 0.85f),
        };
        traps.Add(new TrapInfo
        {
            cell = cell,
            type = type,
            ownerTeam = u.team,
            visual = visual
        });
        Debug.Log($"{u.displayName} は {cell} に{TrapLabel(type)}トラップを設置した（残り {TrapUsesLeft(u)} 回）※本来は敵に非表示だが、共有画面のため表示している");
    }

    // 移動したユニットが敵トラップの半径1マス以内に入っていたら発動させる
    void CheckTraps(Unit moved)
    {
        for (int i = traps.Count - 1; i >= 0; i--)
        {
            TrapInfo t = traps[i];
            if (t.ownerTeam == moved.team) continue;
            if (Vector2.Distance(t.cell, moved.cell) > 1f) continue;

            traps.RemoveAt(i);
            if (t.visual != null) Destroy(t.visual);

            switch (t.type)
            {
                case TrapType.Damage:
                    moved.hp -= 20;
                    Debug.Log($"トラップ発動！ {moved.displayName} はダメージトラップで20ダメージ（固定・防御無視、残りHP {Mathf.Max(moved.hp, 0)}）");
                    if (moved.camouflaged)
                    {
                        moved.camouflaged = false;
                        Debug.Log($"{moved.displayName} は被弾して光学迷彩が解除された");
                    }
                    if (moved.hp <= 0)
                    {
                        Debug.Log($"{moved.displayName} を撃破！");
                        moved.gameObject.SetActive(false);
                        score[t.ownerTeam] += 2;
                        Debug.Log($"チーム{t.ownerTeam} +2点（スコア {score[1]} - {score[2]}）");
                        CheckWin();
                    }
                    break;

                case TrapType.Stun:
                    moved.stunTurns = 1;
                    Debug.Log($"トラップ発動！ {moved.displayName} は行動不能トラップにかかった");
                    break;

                case TrapType.Beacon:
                    moved.beaconed = true;
                    Debug.Log($"トラップ発動！ {moved.displayName} はビーコントラップでビーコン状態になった");
                    break;
            }

            if (!moved.gameObject.activeSelf) return; // 倒れたら以降の判定は不要
        }
    }

    void PlaceSmoke(Unit u, Vector2Int center)
    {
        smokeUses[u] = (smokeUses.TryGetValue(u, out int n) ? n : 0) + 1;
        var cloud = new SmokeCloud { remainingTurnEnds = 2 };
        var dirs = new Vector2Int[]
        {
            new(0, 0), new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        };
        foreach (var d in dirs)
        {
            Vector2Int c = center + d;
            if (!Inside(c)) continue;
            cloud.cells.Add(c);
            var visual = Instantiate(highlightPrefab);
            visual.transform.position = new Vector3(c.x + 0.5f, c.y + 0.5f, 0);
            var sr = visual.GetComponent<SpriteRenderer>();
            sr.sortingOrder = 1;
            sr.color = new Color(0.55f, 0.55f, 0.6f, 0.5f);
            cloud.visuals.Add(visual);
        }
        smokes.Add(cloud);
        Debug.Log($"{u.displayName} は {center} を中心にスモークを展開した（十字5マス、2ターンの終了時まで持続。残り {SmokeUsesLeft(u)} 回）");
    }

    bool IsSmokeCell(Vector2Int cell)
    {
        foreach (var s in smokes)
        {
            if (s.cells.Contains(cell)) return true;
        }
        return false;
    }

    // トラップ設置用：自分のマスと周囲1マスの通行可能なマスを表示
    void ShowTrapRange(Unit unit)
    {
        ClearHighlights();
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                Vector2Int c = unit.cell + new Vector2Int(dx, dy);
                if (!Inside(c)) continue;
                if (terrain != null && !terrain.IsPassable(c)) continue;
                bool hasTrap = false;
                foreach (var t in traps)
                {
                    if (t.cell == c) { hasTrap = true; break; }
                }
                if (hasTrap) continue;
                movable.Add(c);
                AddHighlight(c, new Color(0.9f, 0.4f, 0.1f, 0.35f));
            }
        }
    }

    // 指定した起点・半径の範囲を表示（スモーク設置先の選択用）
    void ShowRadiusFrom(Vector2Int origin, int radius, Color color)
    {
        ClearHighlights();
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                Vector2Int c = origin + new Vector2Int(dx, dy);
                if (!Inside(c)) continue;
                if (Vector2.Distance(origin, c) > radius) continue;
                attackable.Add(c);
                AddHighlight(c, color);
            }
        }
    }

    int OtherTeam(int team) => team == 1 ? 2 : 1;

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
        // 指示を消すと、その操作者のドローンの指示も消える
        DroneInfo drone = FindDroneControlledBy(u);
        if (drone != null) droneOrders.Remove(drone);
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

    // 攻撃すると姿を現し（ブッシュの隠蔽が解ける）、光学迷彩も解除される
    void RevealAttacker(Unit attacker)
    {
        attacker.exposed = true;
        if (attacker.camouflaged)
        {
            attacker.camouflaged = false;
            Debug.Log($"{attacker.displayName} は攻撃のため光学迷彩が解除された");
        }
    }

    void Attack(Unit attacker, Unit target,
                bool guaranteedHit = false, float damageMultiplier = 1f)
    {
        RevealAttacker(attacker);

        // フレンドリーファイア：射撃武器（射程3以上）で、対象との間に
        // 味方がいると25%の確率で誤射し、その味方に通常の攻撃判定を行う
        bool friendlyFire = false;
        if (attacker.weaponRange >= 3)
        {
            Unit inLine = FirstAllyInLine(attacker, target);
            if (inLine != null)
            {
                int ffRoll = Random.Range(1, 101);
                if (ffRoll <= 25)
                {
                    friendlyFire = true;
                    target = inLine;
                    guaranteedHit = false; // 誤射は必中にならない
                    Debug.Log($"フレンドリーファイア！（発生率25%、出目 {ffRoll}）射線にいた {inLine.displayName} を誤射した");
                }
                else
                {
                    Debug.Log($"射線に {inLine.displayName} がいたが誤射はしなかった（発生率25%、出目 {ffRoll}）");
                }
            }
        }

        // 鉄壁：半径2マス以内の味方への攻撃をゴウが引き受ける
        if (!friendlyFire)
        {
            Unit guard = FindGuardFor(target);
            if (guard != null)
            {
                Debug.Log($"{guard.displayName} の鉄壁！ {target.displayName} への攻撃を引き受けた");
                target = guard;
            }
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

        if (hit) ApplyHitDamage(attacker, target, isHammer, damageMultiplier);
        attacker.hasAttacked = true;
        CheckWin();
    }

    // 命中した攻撃のダメージ処理（通常攻撃とグレネードの範囲攻撃で共用）
    // ※キルの得点は「倒されたユニットの相手チーム」に入る
    // 　（範囲攻撃で味方を巻き込んでも、自チームに点は入らない）
    // ※countsForLinkage：連携追撃の起点・2人目に数える攻撃か
    // 　（通常の単体攻撃のみtrue。範囲攻撃と連携追撃そのものはfalse）
    void ApplyHitDamage(Unit attacker, Unit target, bool isHammer,
                        float damageMultiplier = 1f,
                        bool countsForLinkage = true)
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
                            int credit = OtherTeam(attacker.team);
                            score[credit] += 2;
                            Debug.Log($"チーム{credit} +2点（スコア {score[1]} - {score[2]}）");
                        }
                    }
                }
                return;
            }
            Debug.Log($"{target.displayName} のブロック失敗（出目 {blockRoll} > {target.shieldBlockRate}%）");
        }

        target.hp -= damage;
        Debug.Log($"{target.displayName} に {damage} ダメージ（残りHP {Mathf.Max(target.hp, 0)}）");

        // 被弾すると光学迷彩は解除される
        if (target.camouflaged)
        {
            target.camouflaged = false;
            Debug.Log($"{target.displayName} は被弾して光学迷彩が解除された");
        }

        // 状態異常の付与（ダメージが通って、対象が生き残った場合のみ）
        if (target.hp > 0)
        {
            if (HasItem(attacker, "毒付与") && !target.poisoned)
            {
                target.poisoned = true;
                // 毒で倒れた場合の得点先は「倒された側の相手チーム」
                // （誤射や範囲攻撃で味方を毒にした場合も同じ扱い）
                target.poisonSourceTeam = OtherTeam(target.team);
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
            int credit = OtherTeam(target.team);
            score[credit] += 2;
            Debug.Log($"チーム{credit} +2点（スコア {score[1]} - {score[2]}）");
            return;
        }

        // 連携追撃：同じターンに別の味方の単体攻撃が同じ敵に命中したら、
        // 30%で最初の攻撃者が追撃する（通常ダメージの50%、必中、部隊で1回まで）
        if (countsForLinkage && target.team != attacker.team
            && !linkageUsedTeams.Contains(attacker.team))
        {
            if (firstHitBy.TryGetValue(target, out Unit first)
                && first != attacker && first.gameObject.activeSelf)
            {
                int roll = Random.Range(1, 101);
                if (roll <= 30)
                {
                    linkageUsedTeams.Add(attacker.team);
                    bool firstHammer = first.weapon != null
                                       && first.weapon.name == "W_Hammer";
                    Debug.Log($"連携追撃！（発動率30%、出目 {roll}）{first.displayName} が {target.displayName} に追撃する（通常ダメージの50%、必中）");
                    ApplyHitDamage(first, target, firstHammer, 0.5f, false);
                }
                else
                {
                    Debug.Log($"連携追撃は発生しなかった（発動率30%、出目 {roll}）");
                }
            }
            else
            {
                firstHitBy[target] = attacker;
            }
        }
    }

    // 攻撃者と対象の間にいる、最も攻撃者に近い味方ユニット（射線判定）
    Unit FirstAllyInLine(Unit attacker, Unit target)
    {
        Vector2 pa = attacker.cell;
        Vector2 pt = target.cell;
        Vector2 seg = pt - pa;
        float len2 = seg.sqrMagnitude;
        if (len2 < 0.01f) return null;

        Unit best = null;
        float bestT = float.MaxValue;
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (u.team != attacker.team || u == attacker || u == target)
                continue;
            Vector2 pu = u.cell;
            float t = Mathf.Clamp01(Vector2.Dot(pu - pa, seg) / len2);
            Vector2 closest = pa + seg * t;
            if (Vector2.Distance(pu, closest) <= 0.51f && t < bestT)
            {
                best = u;
                bestT = t;
            }
        }
        return best;
    }

    // 到着先が塞がれている場合に、来た方向へ1マスずつ戻って
    // 止まれるマスを探す（見つからなければ出発マス＝その場に留まる）
    Vector2Int FindStopCell(Vector2Int from, Vector2Int dest,
                            bool dronesBlock, DroneInfo selfDrone)
    {
        if (StoppableCell(dest, dronesBlock, selfDrone)) return dest;
        Vector2Int c = dest;
        while (c != from)
        {
            c += new Vector2Int(
                (int)Mathf.Sign(from.x - c.x),
                (int)Mathf.Sign(from.y - c.y));
            if (StoppableCell(c, dronesBlock, selfDrone)) return c;
        }
        return from;
    }

    bool StoppableCell(Vector2Int cell, bool dronesBlock, DroneInfo selfDrone)
    {
        if (!Inside(cell)) return false;
        if (terrain != null && !terrain.IsPassable(cell)) return false;
        if (FindUnitAt(cell) != null) return false;
        if (dronesBlock)
        {
            DroneInfo d = FindDroneAt(cell);
            if (d != null && d != selfDrone) return false;
        }
        return true;
    }

    // グレネードランチャー：地点指定の範囲攻撃＋着弾点の地形破壊
    void ResolveGrenade(Unit attacker, Vector2Int targetCell,
                        int radius, int terrainMult)
    {
        RevealAttacker(attacker); // グレネードも攻撃扱い

        // 命中判定（相手の敏捷性は使わない）
        int hitRate = Mathf.Clamp(
            Mathf.RoundToInt(70f + (attacker.precision - 50) / 2f
                + (attacker.movedThisTurn ? 0 : 10)),
            5, 95);
        int roll = Random.Range(1, 101);
        Vector2Int impact = targetCell;
        if (roll <= hitRate)
        {
            Debug.Log($"{attacker.displayName} のグレネード！ 命中率 {hitRate}% → 出目 {roll} で命中（着弾点 {impact}）");
        }
        else
        {
            // 散布：上下左右・斜めのランダムな1マスへずれる（マップ外なら元のマス）
            var dirs = new Vector2Int[]
            {
                new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
                new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
            };
            Vector2Int shifted = targetCell
                + dirs[Random.Range(0, dirs.Length)];
            if (Inside(shifted)) impact = shifted;
            Debug.Log($"{attacker.displayName} のグレネード！ 命中率 {hitRate}% → 出目 {roll} で外れ、着弾点が {impact} にずれた");
        }

        // 迎撃判定：防御指示中の敵射撃ユニットが着弾点を射程に捉えていれば、
        // 精密÷4 %で撃ち落とす（1人でも成功すれば攻撃全体が無効）
        var interceptors = new List<Unit>();
        foreach (Unit d in FindObjectsByType<Unit>())
        {
            if (d.team == attacker.team) continue;
            if (!d.defending) continue;
            if (d.weaponRange < 3) continue;
            if (Vector2.Distance(d.cell, impact) > d.weaponRange) continue;
            interceptors.Add(d);
        }
        interceptors.Sort((a, b) => b.precision.CompareTo(a.precision));
        foreach (Unit d in interceptors)
        {
            int interceptRate = d.precision / 4;
            int iroll = Random.Range(1, 101);
            if (iroll <= interceptRate)
            {
                Debug.Log($"{d.displayName} がグレネードを撃ち落とした！（迎撃率 {interceptRate}% → 出目 {iroll}）攻撃は無効になった");
                attacker.hasAttacked = true;
                CheckWin();
                return;
            }
            Debug.Log($"{d.displayName} の迎撃は失敗（迎撃率 {interceptRate}% → 出目 {iroll}）");
        }

        // 範囲ダメージ：実際の着弾点から半径内の全ユニット（敵味方問わず）
        // 着弾点のマスは通常ダメージ、周囲のマスは半減
        var victims = new List<Unit>();
        foreach (Unit u in FindObjectsByType<Unit>())
        {
            if (Vector2.Distance(impact, u.cell) <= radius)
                victims.Add(u);
        }
        foreach (Unit v in victims)
        {
            if (!v.gameObject.activeSelf) continue;
            float mult = v.cell == impact ? 1f : 0.5f;
            ApplyHitDamage(attacker, v, false, mult, false);
        }

        // ドローンも爆風を受ける（防御0として計算）
        foreach (DroneInfo d in drones.ToArray())
        {
            if (Vector2.Distance(impact, d.cell) > radius) continue;
            float mult = d.cell == impact ? 1f : 0.5f;
            float raw = (attacker.attack + attacker.weaponPower) / 2f;
            if (!attacker.movedThisTurn) raw *= 1.1f;
            int dmg = Mathf.Max(1, Mathf.RoundToInt(raw * mult));
            DamageDrone(d, dmg);
        }

        // 地形破壊：実際の着弾点のみ（地形へのダメージは防御0で計算）
        if (terrain != null)
        {
            int terrainDamage = Mathf.RoundToInt(
                (attacker.attack + attacker.weaponPower) / 2f) * terrainMult;
            TerrainType destroyed = terrain.DamageTerrain(impact, terrainDamage);
            if (destroyed == TerrainType.Wall)
            {
                Debug.Log($"着弾点 {impact} の壁が破壊された（焦土になった）");
            }
            else if (destroyed == TerrainType.Bridge)
            {
                Debug.Log($"着弾点 {impact} の橋が破壊された！");
                nexusCells.Remove(impact); // 壊れた橋はネクサスでなくなる
                Unit onBridge = FindUnitAt(impact);
                if (onBridge != null) HandleBridgeFall(attacker, onBridge);
            }
            else if (terrain.GetTerrain(impact) == TerrainType.Wall
                     || terrain.GetTerrain(impact) == TerrainType.Bridge)
            {
                Debug.Log($"着弾点 {impact} の地形に {terrainDamage} ダメージ（残り耐久 {terrain.GetTerrainHp(impact)}）");
            }
        }

        attacker.hasAttacked = true;
        CheckWin();
    }

    // 橋が破壊されたとき：上にいたユニットは固定20ダメージ＋最寄りの通行可能マスへ押し出し
    void HandleBridgeFall(Unit attacker, Unit unit)
    {
        unit.hp -= 20;
        Debug.Log($"{unit.displayName} は橋の崩落で20ダメージ（残りHP {Mathf.Max(unit.hp, 0)}）");
        if (unit.hp <= 0)
        {
            Debug.Log($"{unit.displayName} を撃破！");
            unit.gameObject.SetActive(false);
            int credit = OtherTeam(unit.team);
            score[credit] += 2;
            Debug.Log($"チーム{credit} +2点（スコア {score[1]} - {score[2]}）");
            CheckWin();
            return;
        }

        // 最寄りの通行可能マスを探す（同心状に距離1,2,3...と広げる）
        for (int r = 1; r <= 4; r++)
        {
            var candidates = new List<Vector2Int>();
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                    Vector2Int c = unit.cell + new Vector2Int(dx, dy);
                    if (!Inside(c)) continue;
                    if (terrain != null && !terrain.IsPassable(c)) continue;
                    if (FindUnitAt(c) != null) continue;
                    candidates.Add(c);
                }
            }
            if (candidates.Count > 0)
            {
                Vector2Int dest = candidates[Random.Range(0, candidates.Count)];
                Debug.Log($"{unit.displayName} は {dest} へ押し出された");
                unit.cell = dest;
                unit.SnapToCell();
                return;
            }
        }
        Debug.Log($"{unit.displayName} の押し出し先がなかった（その場に留まる）");
    }


    // ===== HUD連携（HudControllerから使う公開API） =====
    public Unit SelectedUnit => selected;

    // HUDのユニットカードをクリックしたとき（盤面でユニットをクリックして選び直すのと同じ扱い）
    public void HudSelect(Unit u)
    {
        if (u == null || !u.gameObject.activeSelf) return;
        if (phase != Phase.Orders) return;
        if (orderingDrone != null || choosingAction) return;
        if (teamReady[u.team])
        {
            Debug.Log($"チーム{u.team} は準備完了済みのため、指示を変更できません");
            return;
        }
        ClearOrder(u);
        Select(u);
        Debug.Log($"{u.displayName} を選択（EX {u.exPoints}/{Unit.MaxEx}）。移動先を指定（自分をクリックで移動しない）");
    }

    // そのユニットの現在の指示を短い文字列で返す（未指示なら ""）
    public string OrderTextFor(Unit u)
    {
        if (u == null || !orders.TryGetValue(u, out Order o)
            || o.prepared == PreparedAction.None)
            return "";
        string move = o.moveCell.HasValue ? $"{o.moveCell.Value} へ移動" : "移動なし";
        string act = o.prepared switch
        {
            PreparedAction.Attack => "攻撃準備",
            PreparedAction.Defend => "防御準備",
            PreparedAction.Special => "特殊：" + (o.specialChoice ?? ""),
            PreparedAction.Ult => "ULT準備",
            _ => ""
        };
        return move + "＋" + act;
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

                // 敵のいるマスは指定できない。味方のいるマスは指定できる
                // （解決時に空いていれば入り、塞がっていれば手前で止まる）
                bool enemyThere = false;
                bool allyThere = false;
                foreach (Unit u in all)
                {
                    if (u == unit || u.cell != c) continue;
                    if (u.team != unit.team) enemyThere = true;
                    else allyThere = true;
                }
                if (enemyThere) continue;

                movable.Add(c);
                AddHighlight(c, allyThere
                    ? new Color(1f, 0.85f, 0.3f, 0.35f) : (Color?)null);
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
