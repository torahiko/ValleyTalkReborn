namespace ValleytalkReborn;

/// <summary>
/// Bark 输出模式——决定命中感知后是自语还是微社交。
/// 命名约束：禁止 "Interactive" 作为成员名（既有 BarkFocusType.Interactive 语义为"注视外界"，N2 风险）。
/// </summary>
internal enum BarkOutputMode { Soliloquy, MicroSocial }

internal enum SensoryType
{
    LewisShorts, TrashOutfit, HazmatSuit, WeddingDress, FaintedYesterday,   // Continuous 组
    GarlicStench, MonsterMusk, Exhaustion                                    // Transient 组
}

internal enum SensoryCategory { Continuous, Transient }
