namespace Rdr2SaveResearch.Persistence;

/// <summary>
/// Policy for campaign-derived state. This is intentionally a classification
/// layer over discovered record descriptors, not a catalogue of weapons or
/// items. Unknown fields are audit-only until a one-change control identifies
/// their reward role.
/// </summary>
public static class CampaignRewardPolicy
{
    public static CampaignCapabilityPolicyDecision Infer(string? semanticLabel) =>
        semanticLabel?.ToLowerInvariant() switch
        {
            "weapon_purchase_eligibility" => new(
                CampaignCapabilitySafety.SharedCapability,
                CampaignRewardKind.ShopAvailability,
                "keyed shop/purchase availability"),
            "mission_completion" or "mission_gate" => new(
                CampaignCapabilitySafety.SharedCapability,
                CampaignRewardKind.MissionGate,
                "mission progression gate"),
            "activity_unlock" => new(
                CampaignCapabilitySafety.SharedCapability,
                CampaignRewardKind.ActivityUnlock,
                "campaign activity availability"),
            "recipe_unlock" => new(
                CampaignCapabilitySafety.SharedCapability,
                CampaignRewardKind.RecipeUnlock,
                "crafting recipe availability"),
            "satchel_capacity" or "satchel_upgrade" => new(
                CampaignCapabilitySafety.SharedCapability,
                CampaignRewardKind.SatchelOrTonicUpgrade,
                "campaign capacity or tonic upgrade"),
            "mission_weapon_entitlement" => new(
                CampaignCapabilitySafety.SharedCapability,
                CampaignRewardKind.MissionWeaponEntitlement,
                "mission-granted weapon entitlement"),
            "money" or "inventory" or "horse" or "bonding" or "health" or
            "weapon" or "weapons" => new(
                CampaignCapabilitySafety.PrivatePlayerState,
                CampaignRewardKind.None,
                "private player state"),
            _ => new(CampaignCapabilitySafety.Unknown,
                CampaignRewardKind.Unknown, "unclassified record")
        };

    public static bool IsShareableRewardKind(CampaignRewardKind kind) => kind is
        CampaignRewardKind.ShopAvailability or
        CampaignRewardKind.MissionGate or
        CampaignRewardKind.ActivityUnlock or
        CampaignRewardKind.RecipeUnlock or
        CampaignRewardKind.SatchelOrTonicUpgrade or
        CampaignRewardKind.MissionWeaponEntitlement;

    public static CampaignRewardKind ParseRewardKind(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "shop" or "shop-availability" => CampaignRewardKind.ShopAvailability,
            "mission-gate" => CampaignRewardKind.MissionGate,
            "activity" or "activity-unlock" => CampaignRewardKind.ActivityUnlock,
            "recipe" or "recipe-unlock" => CampaignRewardKind.RecipeUnlock,
            "satchel" or "satchel-upgrade" or "tonic-upgrade" =>
                CampaignRewardKind.SatchelOrTonicUpgrade,
            "mission-weapon" or "mission-weapon-entitlement" =>
                CampaignRewardKind.MissionWeaponEntitlement,
            _ => throw new CampaignSaveSyncException(
                "--reward-kind must be shop-availability, mission-gate, activity-unlock, " +
                "recipe-unlock, satchel-upgrade, or mission-weapon-entitlement.")
        };
}

public enum CampaignCapabilitySafety
{
    Unknown,
    SharedCapability,
    PrivatePlayerState
}

public enum CampaignRewardKind
{
    Unknown,
    None,
    ShopAvailability,
    MissionGate,
    ActivityUnlock,
    RecipeUnlock,
    SatchelOrTonicUpgrade,
    MissionWeaponEntitlement
}

public readonly record struct CampaignCapabilityPolicyDecision(
    CampaignCapabilitySafety Safety,
    CampaignRewardKind RewardKind,
    string Reason);
