using UnityEngine;

public enum ItemKind { Shield, Armor, Option, Special }

[CreateAssetMenu(fileName = "Item", menuName = "SquadTactics/Equipment Data")]
public class EquipmentData : ScriptableObject
{
    public string itemName = "装備";
    public ItemKind kind = ItemKind.Special;
    public int price = 0;
    public int defenseBonus = 0;    // 防御ボーナス（盾・鎧）
    public int blockRate = 0;       // ブロック率％（盾）※判定は次のステップ
    public int mobilityPenalty = 0; // 機動力ペナルティ
    [TextArea] public string description = "";
}
