using System.Runtime.CompilerServices;
using System;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SpireCodex.Core;

namespace SpireCodex.Replay;

internal static class ReplayHooks
{
    private static int _decision;
    private static string? _decisionType;

    private static PropertyInfo? _selectorProp;

    private static bool _keywordsResolved;

    private const string EnchantSelectType = "deck_select_enchant";
    private const string TransformSelectType = "deck_select_transform";
    private const string UpgradeSelectType = "deck_select_upgrade";

    private static int _pendingReroll;

    public static bool PlayerDied { get; private set; }

    private static string? _pendingBuyId;
    private static string? _pendingBuyKind;

    public static void CloseOpenDecision() => DemoteDecision();

    public static string? ResumeCombatIfInFight()
    {
        try
        {
            var room = Reflect.GetMember(Core.Sts2Access.LiveRunState, "CurrentRoom");
            var combat = Reflect.GetMember(room, "CombatState");
            if (combat == null) return null;
            var encounter = Ids.Bare(Reflect.GetString(Reflect.GetMember(combat, "Encounter"), "Id"));
            if (encounter == null) return null;

            var floorKey = ReplayRecorder.FloorKey;
            _combatFloorKey = floorKey;
            _floorEncounters = new Dictionary<string, int>();
            _combatId = $"{floorKey}:{encounter}";
            _hpLostInCombat = null;
            return _combatId;
        }
        catch { return null; }
    }

    public static void ResetRun()
    {
        PlayerDied = false;
        _pendingBuyId = null;
        _pendingBuyKind = null;
        _combatId = null;
        _combatFloorKey = null;
        _floorEncounters = null;
        _hpLostInCombat = null;
        _combatWon = false;
        _extraTurnPending = null;
        _selectByInstance = null;
        _selected = null;
        _selectDecision = 0;
        _selectDecisionType = null;
        _eventDecision = 0;
        _eventPageIndex = -1;
        _eventId = null;
        ResetAttacks();
        _pendingDeckKind = null;
        _treasureOfferIds = null;
    }

    private static void PurchaseAttempt(object __instance)
    {
        try
        {
            var type = __instance.GetType().Name;
            _pendingBuyKind = type.Contains("Relic") ? "relic"
                : type.Contains("Potion") ? "potion"
                : type.Contains("CardRemoval") ? "removal_service"
                : type.Contains("Card") ? "card" : "other";
            _pendingBuyId = Ids.Bare(Reflect.GetString(Reflect.GetMember(__instance, "Model"), "Id"))
                ?? Ids.Bare(Reflect.GetString(
                       Reflect.GetMember(Reflect.GetMember(__instance, "CreationResult"), "Card"), "Id"));
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static Dictionary<string, int>? _offerIndex;

    private static int _prevDecision;
    private static Dictionary<string, int>? _prevOfferIndex;

    private static HashSet<string>? _ambiguousOffers;

    private static void DemoteDecision()
    {
        FlushSelectOutcome();
        if (_decision > 0)
        {
            _prevDecision = _decision;
            _prevOfferIndex = _offerIndex;
        }
        _decision = 0;
        _decisionType = null;
        _offerIndex = null;
        _ambiguousOffers = null;
    }

    private static void FlushSelectOutcome()
    {
        var picked = _selected;
        var owner = _selectDecision;
        var ownerType = _selectDecisionType;
        _selected = null;
        _selectByInstance = null;
        _selectDecision = 0;
        _selectDecisionType = null;
        if (picked == null || owner <= 0) return;
        ReplayRecorder.Line("outcome")
            ?.Set("decision_id", owner)
            .Set("decision_type", ownerType)
            .Set("outcome", picked.Count > 0 ? "select" : "decline")
            .Set("selected_option_indices", picked)
            .Set("n_selected", picked.Count)
            .Emit();
    }

    private static int? SelectIndexOf(object? card)
    {
        if (_selectByInstance == null || card == null) return null;
        var instance = CardInstances.Of(card);
        if (instance == 0 || !_selectByInstance.TryGetValue(instance, out var idx)) return null;
        var picks = _selected ??= new List<int>();
        if (!picks.Contains(idx)) picks.Add(idx);
        return idx;
    }

    public static void Apply(Harmony harmony)
    {
        var hook = HookPatcher.FindType("MegaCrit.Sts2.Core.Hooks.Hook");
        if (hook == null)
        {
            MainFile.Logger.Info("replay-hooks: Hook type not found; replay disabled");
            return;
        }

        var me = typeof(ReplayHooks);
        var n = 0;
        var attempted = 0;

        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterMapGenerated", me, nameof(MapGenerated));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRoomEntered", me, nameof(RoomEntered));

        var runManager = HookPatcher.FindType("MegaCrit.Sts2.Core.Runs.RunManager");
        attempted++; n += HookPatcher.PatchOn(harmony, runManager, "SetActInternal", me, nameof(ActStarting), 1);
        attempted++; n += HookPatcher.PatchOn(harmony, runManager, "EnterMapCoord", me, nameof(MapCoordChosen), 1);
        attempted++; n += HookPatcher.PatchOn(harmony, runManager, "ExitCurrentRoom", me, nameof(RoomExiting), 0);
        attempted++; n += HookPatcher.PatchOn(harmony, runManager, "ResumePreviousRoom", me, nameof(RoomResuming), 0);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Rooms.MapRoom"),
            "EnterInternal", me, nameof(MapRoomEntered), 2);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.MapCmd"),
            "SetBossEncounter", me, nameof(BossSwapped), 2);
        var mapPoint = HookPatcher.FindType("MegaCrit.Sts2.Core.Map.MapPoint");
        attempted++; n += HookPatcher.PatchOn(harmony, mapPoint, "AddQuest", me, nameof(QuestAdded), 1, postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony, mapPoint, "RemoveQuest", me, nameof(QuestRemoved), 1, postfix: true);

        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeCombatStart", me, nameof(CombatStart));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCombatEnd", me, nameof(CombatEnd));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPlayerTurnStart", me, nameof(PlayerTurnStart));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterSideTurnStart", me, nameof(SideTurnStart));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterTurnEnd|BeforeTurnEnd|AfterSideTurnEnd", me, nameof(TurnEnd));
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeCardPlayed", me, nameof(CardPlayed));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardDrawn", me, nameof(CardDrawn));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardDiscarded", me, nameof(CardDiscarded));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardExhausted", me, nameof(CardExhausted));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterShuffle", me, nameof(Shuffled));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterDamageGiven", me, nameof(DamageGiven));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterDamageReceived", me, nameof(DamageReceived));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPowerAmountChanged", me, nameof(PowerChanged));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterBlockGained", me, nameof(BlockGained));

        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterFlush", me, nameof(HandFlushed));
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeCombatStart", me, nameof(OpeningDrawOrder));
        var cardCmd = HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd");
        attempted++; n += HookPatcher.PatchOn(harmony, cardCmd, "Afflict", me, nameof(CardAfflicted), 3,
                                 firstParamType: "AfflictionModel", postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony, cardCmd, "ClearAffliction", me, nameof(AfflictionCleared), 1,
                                 firstParamType: "CardModel");
        attempted++; n += HookPatcher.Patch(harmony, hook, "ShouldPlay", me, nameof(PlayRefused), postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                                 "MoveToResultPileWithoutPlaying", me, nameof(AutoPlayDeclined), 2);
        _autoPlayType = HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Cards.AutoPlayType");

        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Creatures.Creature"),
            "ClearBlock", me, nameof(BlockClearing), 0);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterBlockCleared", me, nameof(BlockCleared));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPreventingBlockClear", me, nameof(BlockClearPrevented));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterBlockBroken", me, nameof(BlockBroken));
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Creatures.Creature"),
            "LoseBlockInternal", me, nameof(BlockLost), 1);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine"),
            "RollMove", me, nameof(MoveRolled), 3, postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState"),
            "PerformMove", me, nameof(MovePerformed), 1);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CreatureCmd"),
            "Heal", me, nameof(Healed), 3);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCurrentHpChanged", me, nameof(HpChanged));

        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CreatureCmd"),
            "SetMaxHp", me, nameof(MaxHpSet), 2);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CreatureCmd"),
            "SetCurrentHp", me, nameof(CurrentHpSet), 2);
        _combatManagerType = HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatManager");
        if (_combatManagerType == null)
            MainFile.Logger.Info("replay-hooks: CombatManager not found; a monster heal during "
                                 + "combat end may emit a spare hp row, and power_lost rows lose post_combat");

        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterGoldGained", me, nameof(GoldGained));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterOrbChanneled", me, nameof(OrbChanneled));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterOrbEvoked", me, nameof(OrbEvoked));
        var playerCmd = HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.PlayerCmd");
        attempted++; n += HookPatcher.PatchOn(harmony, playerCmd, "LoseGold", me, nameof(GoldLosing), 3);
        attempted++; n += HookPatcher.PatchOn(harmony, playerCmd, "LoseGold", me, nameof(GoldLost), 3, postfix: true);
        _merchantEntryType = HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry");
        var pcs = HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState");
        var cardModel = HookPatcher.FindType("MegaCrit.Sts2.Core.Models.CardModel");
        _cardModelType = cardModel;
        attempted++; n += HookPatcher.PatchOn(harmony, pcs, "set_Energy", me, nameof(EnergySet), 1);
        attempted++; n += HookPatcher.PatchOn(harmony, pcs, "ResetEnergy", me, nameof(EnergyResetting), 0);
        attempted++; n += HookPatcher.PatchOn(harmony, pcs, "AddMaxEnergyToCurrent", me, nameof(EnergyCarrying), 0);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterEnergyReset", me, nameof(EnergyReset));
        attempted++; n += HookPatcher.PatchOn(harmony, cardModel, "SpendResources", me, nameof(CardSpending), 0);
        attempted++; n += HookPatcher.PatchOn(harmony, cardModel, "SpendEnergy", me, nameof(CardEnergyPaying), 1);
        attempted++; n += HookPatcher.PatchOn(harmony, cardModel, "SpendEnergy", me, nameof(CardEnergyPaid), 1, postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.History.CombatHistory"),
            "StarsModified", me, nameof(StarsModified), 3);
        attempted++; n += HookPatcher.PatchOn(harmony, cardModel, "SpendStars", me, nameof(CardStarsPaying), 1);
        attempted++; n += HookPatcher.PatchOn(harmony, cardModel, "SpendStars", me, nameof(CardStarsPaid), 1, postfix: true);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterForge", me, nameof(Forged));
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.OrbCmd"), "Evoke", me, nameof(OrbEvoking), 4);
        var orbModel = HookPatcher.FindType("MegaCrit.Sts2.Core.Models.OrbModel");
        foreach (var orbType in OrbTypesWithPassive(orbModel))
        {
            attempted++; n += HookPatcher.PatchOn(harmony, orbType, "Passive", me, nameof(OrbPassive), 2);
        }

        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.PowerModel"),
            "RemoveInternal", me, nameof(PowerRemoved), 0);
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforePowerAmountChanged", me, nameof(PowerChanging));
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.PowerModel"),
            "ApplyInternal", me, nameof(PowerApplied), 3);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.History.CombatHistory"),
            "PowerReceived", me, nameof(PowerReceived), 4);

        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Rewards.CardReward"),
                                 "Populate", me, nameof(CardRewardPopulated), 0, postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Rewards.CardReward"),
                                 "OnSkipped", me, nameof(CardRewardSkipped), 0);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Rewards.CardReward"),
                                 "Reroll", me, nameof(CardRewardRerolled), 0);

        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "FromDeckGeneric", me, nameof(DeckSelectOffered), 4);

        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "LogChoice", me, nameof(SelectionReturned), 2);

        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "FromDeckForEnchantment", me, nameof(EnchantSelectOffered), 4,
                                 firstParamType: "IReadOnlyList`1");

        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeCardRemoved", me, nameof(CardRemoved));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterItemPurchased", me, nameof(ItemPurchased));
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry"),
            "OnTryPurchaseWrapper", me, nameof(PurchaseAttempt), 2);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardGeneratedForCombat", me, nameof(CardGenerated));
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatState"),
            "CloneCard", me, nameof(CardCloned), 1, postfix: true);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRewardTaken", me, nameof(RewardTaken));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardChangedPiles", me, nameof(CardChangedPiles));

        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                                 "Upgrade", me, nameof(UpgradesStarting), 2, firstParamType: "IEnumerable`1");
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                                 "Upgrade", me, nameof(UpgradesDone), 2, firstParamType: "IEnumerable`1", postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                                 "Downgrade", me, nameof(DowngradeStarting), 1);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                                 "Downgrade", me, nameof(CardDowngraded), 1, postfix: true);

        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Models.CardModel"),
                                 "AfterTransformedFrom", me, nameof(TransformedFrom), 0);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Models.CardModel"),
                                 "AfterTransformedTo", me, nameof(TransformedTo), 0);

        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                                 "Enchant", me, nameof(CardEnchanted), 3,
                                 firstParamType: "EnchantmentModel", postfix: true);

        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.RelicCmd"),
                                 "Obtain", me, nameof(RelicObtained), 3);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.RelicCmd"),
                                 "Remove", me, nameof(RelicRemoved), 1, postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.RelicCmd"),
                                 "Replace", me, nameof(RelicReplacing), 2);
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforePotionUsed", me, nameof(PotionStarting));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPotionUsed", me, nameof(PotionUsed));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPotionProcured", me, nameof(PotionProcured));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPotionDiscarded", me, nameof(PotionDiscarded));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRestSiteHeal", me, nameof(RestHeal));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRestSiteSmith", me, nameof(RestSmith));

        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRoomEntered", me, nameof(RestSiteEntered));
        var restSync = HookPatcher.FindType("MegaCrit.Sts2.Core.Multiplayer.Game.RestSiteSynchronizer");
        var restPatched = 0;
        attempted++; restPatched += HookPatcher.PatchOn(harmony, restSync, "ChooseOption", me,
                                        nameof(RestChoosing), 2, firstParamType: "Player");
        attempted++; restPatched += HookPatcher.PatchOn(harmony, restSync, "ChooseOption", me,
                                        nameof(RestChosen), 2, firstParamType: "Player", postfix: true);
        n += restPatched;
        _restFunnel = restPatched == 2;
        var playerType = HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Players.Player");
        _restOptionsFor = playerType == null ? null
            : restSync?.GetMethod("GetOptionsForPlayer", new[] { playerType });
        if (_restOptionsFor == null)
            MainFile.Logger.Info("replay-hooks: GetOptionsForPlayer(Player) not found; rest rows omit option");
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Models.RelicModel"),
                                 "InvokeDisplayAmountChanged", me, nameof(RelicCounterChanged), 0, postfix: true);

        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.EventModel"),
            "SetEventState", me, nameof(EventPageShown), 2, postfix: true);
        var eventModel = HookPatcher.FindType("MegaCrit.Sts2.Core.Models.EventModel");
        var beginEventArgs = eventModel?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == "BeginEvent" && m.GetParameters().Length == 3) == true ? 3 : 2;
        attempted++; n += HookPatcher.PatchOn(harmony, eventModel,
            "BeginEvent", me, nameof(EventBegun), beginEventArgs);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer"),
            "ChooseOptionForEvent", me, nameof(EventOptionChosen), 2, firstParamType: "Player");
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom"),
            "OptionButtonClicked", me, nameof(EventProceedClicked), 2, firstParamType: "EventOption");

        var eventOption = HookPatcher.FindType("MegaCrit.Sts2.Core.Events.EventOption");
        _optLockedProp = eventOption?.GetProperty("IsLocked",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _optProceedProp = eventOption?.GetProperty("IsProceed",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (_optLockedProp == null || _optProceedProp == null)
            MainFile.Logger.Info("replay-hooks: EventOption.IsLocked/IsProceed not found; "
                                 + "event option selectability and proceed rows are omitted");
        _addVarsMethod = HookPatcher
            .FindType("MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet")
            ?.GetMethod("AddTo", BindingFlags.Public | BindingFlags.Instance);
        if (_addVarsMethod == null)
            MainFile.Logger.Info("replay-hooks: DynamicVarSet.AddTo not found; "
                                 + "event option label/desc are omitted");

        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeAttack", me, nameof(AttackOpened));
        attempted++; n += HookPatcher.Patch(harmony, hook, "ModifyAttackHitCount", me, nameof(AttackHitCount), postfix: true);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterAttack", me, nameof(AttackClosed));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterModifyingDamageAmount", me, nameof(DamageModified));
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeDamageReceived", me, nameof(DamageLanding));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterModifyingHpLostBeforeOsty", me, nameof(HpLossModified));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterModifyingHpLostAfterOsty", me, nameof(HpLossModified));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterModifyingBlockAmount", me, nameof(BlockModified));
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "FromDeckForUpgrade", me, nameof(UpgradeSelectOffered), 2);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "FromDeckForTransformation", me, nameof(TransformSelectOffered), 3);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "FromDeckForRemoval", me, nameof(RemovalSelectEntering), 3);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "FromDeckForRemoval", me, nameof(RemovalSelectLeft), 3, postfix: true);
        var treasureSync = HookPatcher.FindType("MegaCrit.Sts2.Core.Multiplayer.Game.TreasureRoomRelicSynchronizer");
        attempted++; n += HookPatcher.PatchOn(harmony, treasureSync, "BeginRelicPicking", me, nameof(TreasureOffered), 0, postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony, treasureSync, "OnPicked", me, nameof(TreasurePicked), 2);
        attempted++; n += HookPatcher.PatchOn(harmony, treasureSync, "CompleteWithNoRelics", me, nameof(TreasureEmpty), 0);
        var relicReward = HookPatcher.FindType("MegaCrit.Sts2.Core.Rewards.RelicReward");
        attempted++; n += HookPatcher.PatchOn(harmony, relicReward, "OnSelect", me, nameof(RelicRewardSelecting), 0);
        attempted++; n += HookPatcher.PatchOn(harmony, relicReward, "OnSkipped", me, nameof(RelicRewardSkipped), 0);

        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterTakingExtraTurn", me, nameof(ExtraTurnTaken));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterDeath", me, nameof(CreatureDied));
        var creatureCmd = HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CreatureCmd");
        attempted++; n += HookPatcher.PatchOn(harmony, creatureCmd, "Kill", me, nameof(KillStarting), 2,
                                 firstParamType: "IReadOnlyCollection`1");
        attempted++;
        try
        {
            var damageFunnel = creatureCmd?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Damage" && m.GetParameters() is { Length: 7 } ps
                                     && ps[1].ParameterType.Name == "IEnumerable`1"
                                     && ps[2].ParameterType == typeof(decimal));
            if (damageFunnel != null)
            {
                harmony.Patch(damageFunnel, prefix: new HarmonyMethod(typeof(ReplayHooks).GetMethod(
                    nameof(DamageStarting), BindingFlags.NonPublic | BindingFlags.Static)));
                n++;
            }
            else MainFile.Logger.Info("hooks: CreatureCmd.Damage(targets, amount, ...) not found");
        }
        catch (Exception e) { MainFile.Logger.Info($"hooks: patching Damage failed: {e.Message}"); }
        BindCombatEndEvents();

        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatManager"),
            "AfterCreatureAdded", me, nameof(CreatureAdded), 1);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatState"),
            "CreatureEscaped", me, nameof(CreatureEscaped), 1);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.Powers.DoomPower"),
            "DoomKill", me, nameof(DoomKillStarting), 1);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterDiedToDoom", me, nameof(DoomKillDone));
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeSideTurnStart", me, nameof(IntentTurnStarting));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPlayerTurnStart", me, nameof(IntentsShown));
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeTurnEnd|BeforeSideTurnEnd", me, nameof(IntentsCommitted));
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.MonsterModel"),
            "SetMoveImmediate", me, nameof(MoveSetImmediate), 2);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.DevConsole.DevConsole"),
            "ProcessCommand", me, nameof(ConsoleCommand), 3, firstParamType: "Player");

        _selectorProp = Reflect.StaticProperty(
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"), "Selector");
        if (_selectorProp == null)
            MainFile.Logger.Info("replay-hooks: CardSelectCmd.Selector not found; "
                                 + "pick rows will omit `selector`");
        _keywordsResolved = HookPatcher.FindType("MegaCrit.Sts2.Core.Models.CardModel")
            ?.GetProperty("Keywords", BindingFlags.Instance | BindingFlags.Public) != null;
        if (!_keywordsResolved)
            MainFile.Logger.Info("replay-hooks: CardModel.Keywords not found; "
                                 + "play rows will omit `keywords`");

        MainFile.Logger.Info($"replay-hooks: {n}/{attempted} patched");
    }

    private static ReplayLine MapNode(object node)
    {
        var line = new ReplayLine("n")
            .Set("coord", Coord(Reflect.GetMember(node, "coord")))
            .Set("kind", Reflect.GetMember(node, "PointType")?.ToString()?.ToLowerInvariant())
            .Set("children", Enumerate(Reflect.GetMember(node, "Children"))
                .Select(c => Coord(Reflect.GetMember(c, "coord")) ?? "")
                .ToList());
        var quests = QuestIds(node);
        if (quests.Count > 0) line.Set("quests", quests);
        return line;
    }

    private static void MapGenerated(object __0, object __1, int __2)
    {
        try
        {
            FlushPendingAct(__0, __2);
            var line = ReplayRecorder.Line("map");
            if (line == null) return;
            var nodes = new List<ReplayLine>();
            foreach (var node in Enumerate(Reflect.Call(__1, "GetAllMapPoints")))
                nodes.Add(MapNode(node));

            foreach (var extra in new[] { "StartingMapPoint", "BossMapPoint", "SecondBossMapPoint" })
                if (Reflect.GetMember(__1, extra) is { } point)
                {
                    var coord = Coord(Reflect.GetMember(point, "coord"));
                    if (coord == null || nodes.Any(n => (n.Fields.TryGetValue("coord", out var c)
                                                        ? c as string : null) == coord)) continue;
                    nodes.Add(MapNode(point));
                }
            var actModel = ElementAt(Reflect.GetMember(__0, "Acts"), __2);
            line.Set("act", __2 + 1)
                .Set("gen", MapGeneration())
                .Set("boss", Ids.Bare(Reflect.GetString(
                    Reflect.GetMember(actModel, "BossEncounter"), "Id")))
                .Set("boss2", Ids.Bare(Reflect.GetString(
                    Reflect.GetMember(actModel, "SecondBossEncounter"), "Id")))
                .Set("ancient", Ids.Bare(Reflect.GetString(
                    Reflect.GetMember(actModel, "Ancient"), "Id")))
                .Set("ancient_coord", Coord(Reflect.GetMember(
                    Reflect.GetMember(__1, "StartingMapPoint"), "coord")))
                .Set("boss_coord", Coord(Reflect.GetMember(
                    Reflect.GetMember(__1, "BossMapPoint"), "coord")))
                .Set("boss2_coord", Coord(Reflect.GetMember(
                    Reflect.GetMember(__1, "SecondBossMapPoint"), "coord")))
                .Set("nodes", nodes)
                .Emit();
            var points = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var node in Enumerate(Reflect.Call(__1, "GetAllMapPoints"))) points.Add(node);
            foreach (var extra in new[] { "StartingMapPoint", "BossMapPoint", "SecondBossMapPoint" })
                if (Reflect.GetMember(__1, extra) is { } point) points.Add(point);
            _mapPoints = points;
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static int? MapGeneration()
    {
        try
        {
            var mgr = Reflect.GetStatic(
                HookPatcher.FindType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            return Reflect.GetMember(
                Reflect.GetMember(mgr, "MapSelectionSynchronizer"), "MapGenerationCount") as int?;
        }
        catch { return null; }
    }

    private static void RoomEntered(object __0, object __1)
    {
        try
        {
            DemoteDecision();
            _pendingBuyId = null;
            _pendingBuyKind = null;

            var kind = __1.GetType().Name.Replace("Room", "").ToLowerInvariant();
            var roomType = Reflect.GetMember(__1, "RoomType")?.ToString()?.ToLowerInvariant();
            var floor = Reflect.GetInt(__0, "TotalFloor", -1);
            ReplayRecorder.NoteFloor(floor, Reflect.GetInt(__0, "CurrentActIndex", 0) + 1);
            _resumeFrom = null;
            var depth = Reflect.GetMember(__0, "CurrentRoomCount") is int d ? d : (int?)null;
            ReplayRecorder.Line("room")
                ?.Set("kind", kind)
                .Set("room_type", roomType)
                .Set("depth", depth)
                .Set("point_type", depth == 1 ? PointTypeOf(__0, __1) : null)
                .Set("floor", floor >= 0 ? floor : (int?)null)
                .Set("coord", Coord(Reflect.GetMember(__0, "CurrentMapCoord")))
                .Set("id", Ids.Bare(Reflect.GetString(Reflect.GetMember(__1, "CanonicalEvent"), "Id"))
                           ?? Ids.Bare(Reflect.GetString(__1, "ModelId")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static HashSet<object>? _mapPoints;

    private static object? _resumeFrom;

    private static (object State, int Index, string? Name)? _pendingAct;

    private static string RoomKind(object room)
        => room.GetType().Name.Replace("Room", "").ToLowerInvariant();

    private static List<string> QuestIds(object point)
        => Enumerate(Reflect.GetMember(point, "Quests"))
            .Select(q => Ids.Bare(Reflect.GetString(q, "Id")))
            .Where(id => id != null).Select(id => id!).ToList();

    private static void ActStarting(object __instance, int __0)
    {
        try
        {
            var state = Reflect.GetMember(__instance, "State");
            if (state == null) return;
            var name = Ids.Bare(Reflect.GetString(ElementAt(Reflect.GetMember(state, "Acts"), __0), "Id"));
            ReplayRecorder.NoteFloor(-1, __0 + 1);
            _pendingAct = null;
            if (ReplayRecorder.Line("act") is { } line)
                line.Set("act", __0 + 1).Set("name", name).Emit();
            else
                _pendingAct = (state, __0, name);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void FlushPendingAct(object runState, int actIndex)
    {
        var pending = _pendingAct;
        _pendingAct = null;
        if (pending is not { } p || !ReferenceEquals(p.State, runState) || p.Index != actIndex) return;
        ReplayRecorder.Line("act")?.Set("act", actIndex + 1).Set("name", p.Name).Emit();
    }

    private static string? PointTypeOf(object runState, object room)
    {
        var entry = Reflect.GetMember(runState, "CurrentMapPointHistoryEntry");
        var first = Enumerate(Reflect.GetMember(entry, "Rooms")).FirstOrDefault();
        if (first == null) return null;
        var roomType = Reflect.GetString(room, "RoomType");
        if (roomType == null || Reflect.GetString(first, "RoomType") != roomType) return null;
        if (Reflect.GetString(first, "ModelId") != Reflect.GetString(room, "ModelId")) return null;
        return Reflect.GetString(entry, "MapPointType")?.ToLowerInvariant();
    }

    private static void MapCoordChosen(object __instance, object __0)
    {
        try
        {
            var state = Reflect.GetMember(__instance, "State");
            var map = Reflect.GetMember(state, "Map");
            var pick = Coord(__0);
            if (map == null || pick == null) return;
            var visited = Enumerate(Reflect.GetMember(state, "VisitedMapCoords")).Select(Coord).ToList();
            if (visited.Count == 0 || visited.Contains(pick)) return;

            var from = Reflect.GetMember(state, "CurrentMapPoint");
            if (from == null || ReferenceEquals(from, Reflect.GetMember(map, "BossMapPoint"))) return;
            if (Reflect.GetMember(Reflect.GetMember(from, "coord"), "row") is not int fromRow
                || Reflect.Call(map, "GetRowCount") is not int rows
                || fromRow == rows - 1) return;

            var mapTravel = HookPatcher.FindType("MegaCrit.Sts2.Core.Map.MapTravel");
            var offer = mapTravel?.GetMethod("GetTravelablePointsFrom", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { state, from });
            var coords = Enumerate(offer)
                .Select(p => Reflect.GetMember(p, "coord"))
                .OrderBy(c => Reflect.GetMember(c, "col") is int col ? col : int.MaxValue)
                .Select(Coord).Where(c => c != null).Select(c => c!).ToList();
            if (coords.Count < 2) return;

            if (ReplayRecorder.Line("decision") is not { } line) return;
            var id = ReplayRecorder.NextDecisionId();
            var options = coords.Select((c, i) => new ReplayLine("o")
                .Set("option_index", i)
                .Set("option_kind", "map_node")
                .Set("option_id", c)
                .SetFlag("presented", true)
                .SetFlag("selectable", true)).ToList();
            line.Set("decision_id", id)
                .Set("decision_type", "map_node")
                .Set("source", "map")
                .Set("from", Coord(Reflect.GetMember(from, "coord")))
                .Set("n_presented", coords.Count)
                .Set("n_selectable", coords.Count);
            if (HookPatcher.FindType("MegaCrit.Sts2.Core.Hooks.Hook")
                    ?.GetMethod("ShouldAllowFreeTravel", BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, new[] { state }) is bool free)
                line.SetFlag("free_travel", free);
            line.Set("options", options).Emit();

            var index = coords.IndexOf(pick);
            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", id)
                .Set("decision_type", "map_node")
                .Set("outcome", "chosen")
                .Set("option_index", index >= 0 ? index : (int?)null)
                .Set("option_id", pick)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void RoomResuming(object __instance)
    {
        try { _resumeFrom = Reflect.GetMember(Reflect.GetMember(__instance, "State"), "CurrentRoom"); }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void RoomExiting(object __instance)
    {
        try
        {
            var state = Reflect.GetMember(__instance, "State");
            var room = Reflect.GetMember(state, "CurrentRoom");
            var resuming = room != null && ReferenceEquals(room, _resumeFrom);
            _resumeFrom = null;
            if (room == null) return;
            var depth = Reflect.GetMember(state, "CurrentRoomCount") is int d ? d : (int?)null;
            ReplayRecorder.Line("room_exit")
                ?.Set("kind", RoomKind(room))
                .Set("depth", depth)
                .Emit();
            if (!resuming || depth is not int dd || dd < 2) return;
            var below = ElementAt(Reflect.GetMember(state, "_currentRooms"), dd - 2);
            if (below == null) return;
            ReplayRecorder.Line("room_resume")
                ?.Set("kind", RoomKind(below))
                .Set("depth", dd - 1)
                .Set("id", Ids.Bare(Reflect.GetString(Reflect.GetMember(below, "CanonicalEvent"), "Id"))
                           ?? Ids.Bare(Reflect.GetString(below, "ModelId")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void MapRoomEntered(object __0, bool __1)
    {
        try
        {
            if (__1) return;
            ReplayRecorder.Line("map_room")
                ?.Set("act", Reflect.GetMember(__0, "CurrentActIndex") is int a ? a + 1 : (int?)null)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void BossSwapped(object __0, object __1)
    {
        try
        {
            if (Reflect.GetString(__1, "RoomType") != "Boss") return;
            var act = Reflect.GetMember(__0, "Act");
            var prev = Ids.Bare(Reflect.GetString(Reflect.GetMember(act, "BossEncounter"), "Id"));
            var boss = Ids.Bare(Reflect.GetString(__1, "Id"));
            if (boss == null || boss == prev) return;
            ReplayRecorder.Line("boss_swap")
                ?.Set("act", Reflect.GetMember(__0, "CurrentActIndex") is int a ? a + 1 : (int?)null)
                .Set("boss", boss)
                .Set("prev", prev)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void QuestAdded(object __instance, object __0) => QuestChanged("add", __instance, __0);
    private static void QuestRemoved(object __instance, object __0) => QuestChanged("remove", __instance, __0);

    private static void QuestChanged(string op, object point, object model)
    {
        try
        {
            if (_mapPoints == null || !_mapPoints.Contains(point)) return;
            ReplayRecorder.Line("quest")
                ?.Set("op", op)
                .Set("coord", Coord(Reflect.GetMember(point, "coord")))
                .Set("id", Ids.Bare(Reflect.GetString(model, "Id")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static readonly ConditionalWeakTable<object, object> MoveOwners = new();

    private static void MoveRolled(object __result, object __1)
    {
        try
        {
            if (__result == null || __1 == null) return;
            MoveOwners.Remove(__result);
            MoveOwners.Add(__result, __1);
            if (IntentsShownFor(Reflect.GetMember(__1, "CombatState")))
                EmitIntent(__1, __result, "roll");
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void MovePerformed(object __instance)
    {
        try
        {
            if (!MoveOwners.TryGetValue(__instance, out var owner)) return;
            var intents = IntentTypes(__instance);
            ReplayRecorder.Line("move")
                ?.Set("src", CreatureRef(owner))
                .Set("src_cid", CreatureSlots.Maybe(owner))
                .Set("id", Reflect.GetString(__instance, "StateId"))
                .Set("intents", intents.Count > 0 ? intents : null)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static string CreatureRef(object? creature)
        => creature == null ? "effect"
            : Reflect.GetMember(creature, "IsPlayer") is true ? "player"
            : Ids.Bare(Reflect.GetString(creature, "ModelId")) ?? "unknown";

    private static int? IntOf(object? target, string name)
        => Reflect.GetMember(target, name) is int i ? i : (int?)null;

    private static List<string>? DamageProps(object? props)
    {
        var raw = props?.ToString();
        if (string.IsNullOrEmpty(raw) || raw == "0") return null;
        var names = raw.Split(',').Select(x => x.Trim().ToLowerInvariant())
                       .Where(x => x.Length > 0).ToList();
        return names.Count > 0 ? names : null;
    }

    private static string? _combatId;
    private static Dictionary<string, int>? _floorEncounters;
    private static string? _combatFloorKey;

    private static int? _hpLostInCombat;

    private static Dictionary<int, int>? _selectByInstance;
    private static List<int>? _selected;

    private static int _selectDecision;
    private static string? _selectDecisionType;

    private static void CombatStart(object __1)
    {
        try
        {
            ReplayRecorder.ResetFaults();
            _combatWon = false;
            _extraTurnPending = null;
            _doomed.Clear();

            var line = ReplayRecorder.Line("combat_start");
            if (line == null) return;

            var encounter = Ids.Bare(Reflect.GetString(Reflect.GetMember(__1, "Encounter"), "Id"));
            var floorKey = ReplayRecorder.FloorKey;
            if (floorKey != _combatFloorKey)
            {
                _combatFloorKey = floorKey;
                _floorEncounters = new Dictionary<string, int>();
            }
            var key = encounter ?? "unknown";
            var seen = (_floorEncounters ??= new Dictionary<string, int>())
                .TryGetValue(key, out var prior) ? prior : 0;
            _floorEncounters[key] = seen + 1;
            _combatId = seen == 0 ? $"{floorKey}:{key}" : $"{floorKey}:{key}#{seen}";

            _hpLostInCombat = 0;

            var enemies = new List<ReplayLine>();
            var i = 0;
            foreach (var e in Enumerate(Reflect.GetMember(__1, "Enemies")))
                enemies.Add(CreatureEntry(new ReplayLine("e").Set("i", i++), e));

            List<ReplayLine>? allies = null;
            if (Reflect.GetMember(__1, "Allies") is IEnumerable allySeq)
            {
                allies = new List<ReplayLine>();
                foreach (var a in Enumerate(allySeq))
                    if (Reflect.GetMember(a, "IsPlayer") is false)
                        allies.Add(CreatureEntry(new ReplayLine("a"), a));
            }
            line.Set("combat_id", _combatId)
                .Set("attempt_id", ReplayRecorder.AttemptId)
                .Set("encounter", encounter)
                .Set("rng_state", RngState.Read())
                .Set("enemies", enemies)
                .Set("allies", allies)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CombatEnd(object __1) => EmitCombatEnd(__1, won: true);

    private static void PlayerTurnStart(object __0, object __2)
    {
        try
        {
            var extra = TakeExtraTurnMark(__2);
            ReplayRecorder.Line("turn")
                ?.Set("combat_id", _combatId)
                .Set("attempt_id", _combatId == null ? (int?)null : ReplayRecorder.AttemptId)
                .Set("n", TurnNumberOf(__2))
                .Set("round", RoundOf(__0))
                .Set("extra", extra ? (bool?)true : null)
                .Set("side", "player")
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void SideTurnStart(object __0, object __1)
    {
        try
        {
            var side = __1?.ToString()?.ToLowerInvariant();
            if (side == "player") return;
            ReplayRecorder.Line("turn")
                ?.Set("combat_id", _combatId)
                .Set("attempt_id", _combatId == null ? (int?)null : ReplayRecorder.AttemptId)
                .Set("n", LocalTurnNumber(__0))
                .Set("round", RoundOf(__0))
                .Set("side", side)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void TurnEnd(object __0, object __1)
    {
        try
        {
            ReplayRecorder.Line("end_turn")
                ?.Set("n", LocalTurnNumber(__0))
                .Set("round", RoundOf(__0))
                .Set("side", __1?.ToString()?.ToLowerInvariant())
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static bool _combatWon;

    private static List<object>? _extraTurnPending;

    private static Delegate? _onCombatEnded;
    private static Delegate? _onCombatWon;

    private static void ExtraTurnTaken(object __1)
    {
        try
        {
            if (__1 == null) return;
            (_extraTurnPending ??= new List<object>()).Add(__1);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static readonly ConditionalWeakTable<object, string> KilledBy = new();
    private static readonly ConditionalWeakTable<object, string> DamagedBy = new();

    private static void KillStarting(object __0)
    {
        try
        {
            if (CallerId() is not { } by) return;
            foreach (var c in Enumerate(__0))
            {
                KilledBy.Remove(c);
                KilledBy.Add(c, by);
            }
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void DamageStarting(object __1, object? __4, object? __5)
    {
        try
        {
            if (__4 != null || __5 != null) return;
            if (CallerId() is not { } by) return;
            foreach (var t in Enumerate(__1))
            {
                DamagedBy.Remove(t);
                DamagedBy.Add(t, by);
            }
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static string? CallerId()
    {
        var owner = CallerOf("MegaCrit.Sts2.Core.Commands.CreatureCmd", out _);
        if (owner == null) return null;
        var name = owner.Name;
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(name[i]));
        }
        return sb.ToString();
    }

    private static void CreatureDied(object __2, bool __3)
    {
        try
        {
            ReplayRecorder.Line("death")
                ?.Set("tgt", CreatureRef(__2))
                .Set("tgt_cid", CreatureSlots.Maybe(__2))
                .SetFlag("removal_prevented", __3)
                .Set("cause", _doomed.Contains(__2) ? "doom" : null)
                .Set("killed_by", KilledBy.TryGetValue(__2, out var by) ? by : null)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CombatWonEvent(object room) => _combatWon = true;

    private static void CombatEndedEvent(object room)
    {
        try
        {
            EmitCombatEnd(Reflect.GetMember(room, "CombatState"), _combatWon);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void EmitCombatEnd(object? combatState, bool won)
    {
        try
        {
            if (_combatId == null) return;
            ReplayRecorder.Line("combat_end")
                ?.Set("result", won ? "victory" : "loss")
                .Set("combat_id", _combatId)
                .Set("attempt_id", ReplayRecorder.AttemptId)
                .Set("turns", LocalTurnNumber(combatState))
                .Set("rounds", RoundOf(combatState))
                .Set("hp_lost_total", _hpLostInCombat)
                .Emit();
            _combatId = null;
            _hpLostInCombat = null;
            _extraTurnPending = null;
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void BindCombatEndEvents()
    {
        var type = HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatManager");
        var instance = Reflect.GetStatic(type, "Instance");
        if (type == null || instance == null)
        {
            MainFile.Logger.Info("replay-hooks: CombatManager.Instance not found; "
                                 + "combat_end will not fire on a loss");
            return;
        }
        _onCombatEnded = Rebind(type, instance, "CombatEnded", nameof(CombatEndedEvent), _onCombatEnded);
        _onCombatWon = Rebind(type, instance, "CombatWon", nameof(CombatWonEvent), _onCombatWon);
    }

    private static Delegate? Rebind(Type type, object instance, string eventName,
                                    string handlerName, Delegate? installed)
    {
        try
        {
            var evt = type.GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance);
            if (evt?.EventHandlerType == null)
            {
                MainFile.Logger.Info($"replay-hooks: CombatManager.{eventName} not found");
                return null;
            }
            if (installed != null) evt.RemoveEventHandler(instance, installed);
            var handler = typeof(ReplayHooks).GetMethod(
                handlerName, BindingFlags.NonPublic | BindingFlags.Static);
            if (handler == null) return null;
            var d = Delegate.CreateDelegate(evt.EventHandlerType, handler);
            evt.AddEventHandler(instance, d);
            return d;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info(
                $"replay-hooks: subscribing to CombatManager.{eventName} failed: {e.Message}");
            return null;
        }
    }

    private static int? RoundOf(object? combatState)
    {
        var n = Reflect.GetInt(combatState, "RoundNumber", 0);
        return n > 0 ? n : (int?)null;
    }

    private static int? TurnNumberOf(object? player)
    {
        var pcs = Reflect.GetMember(player, "PlayerCombatState");
        if (pcs == null) return null;
        var n = Reflect.GetInt(pcs, "TurnNumber", 0);
        return n > 0 ? n : (int?)null;
    }

    private static int? LocalTurnNumber(object? combatState)
    {
        var players = Enumerate(Reflect.GetMember(combatState, "Players")).ToList();
        if (players.Count == 0) return null;
        var me = players.FirstOrDefault(p => LocalPlayer.IsLocalPlayer(p))
                 ?? (players.Count == 1 ? players[0] : null);
        return TurnNumberOf(me);
    }

    private static bool TakeExtraTurnMark(object? player)
    {
        var pending = _extraTurnPending;
        if (pending == null || player == null) return false;
        for (var i = 0; i < pending.Count; i++)
        {
            if (!ReferenceEquals(pending[i], player)) continue;
            pending.RemoveAt(i);
            return true;
        }
        return false;
    }

    private static void CardPlayed(object __0, object __1)
    {
        try
        {
            var card = Reflect.GetMember(__1, "Card");
            var target = Reflect.GetMember(__1, "Target");
            var play = ReplayRecorder.Line("play");
            if (play == null) return;
            play.Set("c", CardInstances.Of(card))
                .Set("id", Ids.Bare(Reflect.GetString(card, "Id")))
                .Set("up", Reflect.GetInt(card, "CurrentUpgradeLevel", 0))
                .Set("target", target == null ? null : Ids.Bare(Reflect.GetString(target, "ModelId")))
                .SetFlag("auto", Reflect.GetBool(__1, "IsAutoPlay"))
                .Set("play_index", Reflect.GetInt(__1, "PlayIndex", 0))
                .Set("play_count", Reflect.GetInt(__1, "PlayCount", 1))
                .Set("turn", Reflect.GetInt(__0, "RoundNumber", 0));
            try
            {
                var origin = CardInstances.DeckIdOf(card);
                play.Set("deck_c", origin > 0 ? origin : (int?)null)
                    .Set("target_cid", CreatureSlots.Maybe(target));
            }
            catch (Exception e) { ReplayRecorder.Fault(e); }
            try
            {
                var resources = Reflect.GetMember(__1, "Resources");
                var energyCost = Reflect.GetMember(card, "EnergyCost");
                var xValue = Reflect.GetBool(energyCost, "CostsX")
                    ? Reflect.GetMember(energyCost, "CapturedXValue") as int?
                    : null;
                var keywords = _keywordsResolved
                    ? Enumerate(Reflect.GetMember(card, "Keywords"))
                          .Select(k => k.ToString()!.ToLowerInvariant()).ToList()
                    : null;
                play.Set("cost_paid", Reflect.GetInt(resources, "EnergySpent", -1))
                    .Set("cost_value", Reflect.GetMember(resources, "EnergyValue") as int?)
                    .Set("stars_paid", Reflect.GetInt(resources, "StarsSpent", 0))
                    .Set("stars_value", Reflect.GetMember(resources, "StarValue") as int?)
                    .Set("x_value", xValue)
                    .Set("result_pile",
                         Reflect.GetMember(__1, "ResultPile")?.ToString()?.ToLowerInvariant())
                    .Set("keywords", keywords is { Count: > 0 } ? keywords : null);
            }
            catch (Exception e) { ReplayRecorder.Fault(e); }
            play.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CardDrawn(object __2, bool __3) => CardMove("draw", __2, "turn_start", __3);
    private static void CardDiscarded(object __2) => CardMove("discard", __2);

    private static void CardExhausted(object __2, bool __3)
        => CardMove("exhaust", __2, "ethereal", __3);

    private static void CardMove(string kind, object card, string? reason = null, bool value = false)
    {
        try
        {
            var line = ReplayRecorder.Line(kind);
            if (line == null) return;
            line.Set("c", CardInstances.Of(card))
                .Set("id", Ids.Bare(Reflect.GetString(card, "Id")));
            if (reason != null) line.SetFlag(reason, value);
            try
            {
                var origin = CardInstances.DeckIdOf(card);
                line.Set("deck_c", origin > 0 ? origin : (int?)null);
            }
            catch (Exception e) { ReplayRecorder.Fault(e); }
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CardCloned(object __0, object __result)
    {
        try { CardInstances.LinkClone(__0, __result); } catch { }
    }

    private static void CardGenerated(object __1)
    {
        try
        {
            var origin = CardInstances.DeckIdOf(__1);
            ReplayRecorder.Line("generate")
                ?.Set("c", CardInstances.Of(__1))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void Shuffled(object __2)
    {
        try
        {
            var cards = Reflect.GetMember(
                Reflect.GetMember(Reflect.GetMember(__2, "PlayerCombatState"), "DrawPile"),
                "Cards");
            ReplayRecorder.Line("shuffle")
                ?.Set("mine", Mine(__2))
                .Set("n_draw", cards == null ? (int?)null : Enumerate(cards).Count())
                .Set("order_c", PileOrder(cards))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static Type? _autoPlayType;

    private static List<int>? PileOrder(object? cards)
        => cards is IEnumerable ? Enumerate(cards).Select(CardInstances.Of).ToList() : null;

    private static void HandFlushed(object __1, object __3, object __4)
    {
        try
        {
            ReplayRecorder.Line("flush")
                ?.Set("mine", Mine(__1))
                .Set("flushed_c", PileOrder(__3))
                .Set("retained_c", PileOrder(__4))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void OpeningDrawOrder(object __1)
    {
        try
        {
            var players = Enumerate(Reflect.GetMember(__1, "Players")).ToList();
            var me = players.FirstOrDefault(p => LocalPlayer.IsLocalPlayer(p))
                     ?? (players.Count == 1 ? players[0] : null);
            if (me == null) return;
            var cards = Reflect.GetMember(
                Reflect.GetMember(Reflect.GetMember(me, "PlayerCombatState"), "DrawPile"), "Cards");
            var order = PileOrder(cards);
            if (order == null) return;
            var deck = Enumerate(cards).Select(CardInstances.DeckIdOf).ToList();
            ReplayRecorder.Line("draw_order")
                ?.Set("order_c", order)
                .Set("order_deck_c", deck.Contains(0) ? null : deck)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CardAfflicted(object __1, decimal __2, object? __result)
    {
        try
        {
            if (__result is not System.Threading.Tasks.Task { IsCompletedSuccessfully: true } done) return;
            var applied = Reflect.GetMember(done, "Result");
            if (applied == null || __1 == null) return;
            var origin = CardInstances.DeckIdOf(__1);
            ReplayRecorder.Line("afflict")
                ?.Set("c", CardInstances.Of(__1))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Set("affliction", Ids.Bare(Reflect.GetString(applied, "Id")))
                .Set("amount", (int)__2)
                .Set("amount_total", Reflect.GetMember(applied, "Amount") as int?)
                .Set("mine", Mine(Reflect.GetMember(__1, "Owner")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void AfflictionCleared(object __0)
    {
        try
        {
            var affliction = Reflect.GetMember(__0, "Affliction");
            if (affliction == null) return;
            var origin = CardInstances.DeckIdOf(__0);
            ReplayRecorder.Line("unafflict")
                ?.Set("c", CardInstances.Of(__0))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Set("affliction", Ids.Bare(Reflect.GetString(affliction, "Id")))
                .Set("amount", Reflect.GetMember(affliction, "Amount") as int?)
                .Set("mine", Mine(Reflect.GetMember(__0, "Owner")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static object? _vetoedCard;
    private static object? _vetoPreventer;
    private static int _vetoAutoType;

    private static void PlayRefused(bool __result, object __1, object? __2, int __3)
    {
        if (__result || __3 == 0) return;
        _vetoedCard = __1;
        _vetoPreventer = __2;
        _vetoAutoType = __3;
    }

    private static void AutoPlayDeclined(object __1)
    {
        try
        {
            var vetoed = ReferenceEquals(__1, _vetoedCard);
            var reason = Enumerate(Reflect.GetMember(__1, "Keywords"))
                             .Any(k => k?.ToString() == "Unplayable") ? "unplayable"
                : vetoed ? "blocked"
                : "no_target";
            if (vetoed) _vetoedCard = null;
            var blocked = reason == "blocked";
            var kind = blocked && _autoPlayType != null ? Enum.GetName(_autoPlayType, _vetoAutoType) : null;
            var origin = CardInstances.DeckIdOf(__1);
            ReplayRecorder.Line("play_blocked")
                ?.Set("c", CardInstances.Of(__1))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Set("up", Reflect.GetInt(__1, "CurrentUpgradeLevel", 0))
                .Set("reason", reason)
                .Set("preventer", blocked ? Ids.Bare(Reflect.GetString(_vetoPreventer, "Id")) : null)
                .Set("auto_type", kind?.ToLowerInvariant())
                .Set("mine", Mine(Reflect.GetMember(__1, "Owner")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void DamageGiven(object __1, object __2, object __3, object __4, object __5, object __6)
    {
        try
        {
            var dealerIsPlayer = Reflect.GetMember(__2, "IsPlayer") is true;
            var targetIsPlayer = Reflect.GetMember(__5, "IsPlayer") is true;
            if (targetIsPlayer && Reflect.GetBool(__3, "WasTargetKilled")) PlayerDied = true;
            if (targetIsPlayer && _hpLostInCombat is { } lost)
                _hpLostInCombat = lost + Reflect.GetInt(__3, "UnblockedDamage", 0);
            var hit = ReplayRecorder.Line("hit");
            if (hit == null)
            {
                TakeDamageFrame(__1, __5, __2, __6);
                return;
            }
            hit.Set("src", dealerIsPlayer ? "player"
                        : __2 == null ? "effect" : Ids.Bare(Reflect.GetString(__2, "ModelId")))
                .Set("dst", targetIsPlayer ? "player" : Ids.Bare(Reflect.GetString(__5, "ModelId")))
                .Set("dmg", Reflect.GetInt(__3, "UnblockedDamage", 0))
                .Set("blocked", Reflect.GetInt(__3, "BlockedDamage", 0))
                .Set("overkill", IntOf(__3, "OverkillDamage") is int over && over > 0 ? over : (int?)null)
                .Set("full_block", Reflect.GetMember(__3, "WasFullyBlocked") is true ? true : (bool?)null)
                .SetFlag("killed", Reflect.GetBool(__3, "WasTargetKilled"))
                .Set("card", __6 == null ? null : Ids.Bare(Reflect.GetString(__6, "Id")));
            try
            {
                var why = TakeDamageFrame(__1, __5, __2, __6);
                hit.Set("src_cid", CreatureSlots.Maybe(__2))
                    .Set("dst_cid", CreatureSlots.Maybe(__5))
                    .Set("dmg_type", DamageProps(__4))
                    .Set("effect", __2 == null && __6 == null && DamagedBy.TryGetValue(__5, out var by) ? by : null)
                    .Set("atk", AttackIdFor(__1, __2, __6, __4))
                    .Set("mods", why?.Mods)
                    .Set("hp_mods", why?.HpMods);
            }
            catch (Exception e) { ReplayRecorder.Fault(e); }
            hit.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void DamageReceived(object __2, object __3, object __4, object __5)
    {
        try
        {
            if (__2 != null) return;
            if (Reflect.GetMember(__3, "IsPlayer") is not true) return;
            ReplayRecorder.Line("hp_loss")
                ?.Set("dmg", Reflect.GetInt(__4, "UnblockedDamage", 0))
                .Set("blocked", Reflect.GetInt(__4, "BlockedDamage", 0))
                .Set("overkill", IntOf(__4, "OverkillDamage") is int over && over > 0 ? over : (int?)null)
                .Set("dmg_type", DamageProps(__5))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void PowerChanged(object __2, decimal __3, object __4)
    {
        try
        {
            var owner = Reflect.GetMember(__2, "Owner");
            var line = ReplayRecorder.Line("power");
            if (line == null) return;
            var src = CreatureRef(__4);
            var landed = (int)__3;
            line.Set("src", src)
                .Set("src_cid", CreatureSlots.Maybe(__4))
                .Set("id", Ids.Bare(Reflect.GetString(__2, "Id")))
                .Set("n", landed)
                .Set("amount", AmountOf(__2))
                .Set("n_intended", IntendedAmount(__2, src) is { } asked && asked != landed
                        ? asked : (int?)null)
                .Set("tgt", Ids.Bare(Reflect.GetString(owner, "ModelId")))
                .Set("tgt_cid", CreatureSlots.Maybe(owner));
            StampInstance(line, __2);
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void PowerChanging(object __1, decimal __2, object __4, object __5)
    {
        try
        {
            if (__1 == null) return;
            Intents.Remove(__1);
            Intents.Add(__1, new Intent
            {
                Amount = (int)__2,
                Src = CreatureRef(__4),
                SrcCid = CreatureSlots.Maybe(__4),
                Card = __5 == null ? null : Ids.Bare(Reflect.GetString(__5, "Id")),
            });
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void PowerApplied(object __instance, object __0, decimal __1)
    {
        try
        {
            if (__1 != 0m) return;
            EmitNegated(__instance, __0, Reflect.GetMember(__instance, "Applier"), onBoard: false);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void PowerReceived(object __1, decimal __2, object __3)
    {
        try
        {
            if ((int)__2 != 0) return;
            EmitNegated(__1, Reflect.GetMember(__1, "Owner"), __3, onBoard: true);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void EmitNegated(object? power, object? target, object? applier, bool onBoard)
    {
        var line = ReplayRecorder.Line("power_negated");
        if (line == null) return;
        Intent? intent = null;
        if (power != null) Intents.TryGetValue(power, out intent);
        line.Set("src", intent?.Src ?? CreatureRef(applier))
            .Set("src_cid", intent?.SrcCid ?? CreatureSlots.Maybe(applier))
            .Set("id", Ids.Bare(Reflect.GetString(power, "Id")))
            .Set("n_intended", intent?.Amount)
            .Set("card", intent?.Card)
            .Set("tgt", Ids.Bare(Reflect.GetString(target, "ModelId")))
            .Set("tgt_cid", CreatureSlots.Maybe(target));
        if (onBoard)
        {
            line.Set("amount", AmountOf(power));
            StampInstance(line, power);
        }
        line.Emit();
    }

    private static void PowerRemoved(object __instance)
    {
        try
        {
            if (Reflect.Call(__instance, "ShouldRemoveDueToAmount") is true) return;
            var owner = Reflect.GetMember(__instance, "Owner");
            var line = ReplayRecorder.Line("power_lost");
            if (line == null) return;
            var held = AmountOf(__instance);
            line.Set("id", Ids.Bare(Reflect.GetString(__instance, "Id")))
                .Set("n", held is { } a ? -a : (int?)null)
                .Set("amount", 0)
                .Set("tgt", Ids.Bare(Reflect.GetString(owner, "ModelId")))
                .Set("tgt_cid", CreatureSlots.Maybe(owner))
                .Set("dead", Reflect.GetMember(owner, "IsAlive") is false ? (object)true : null)
                .Set("post_combat", PostCombat());
            StampInstance(line, __instance);
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static int? AmountOf(object? power)
        => Reflect.GetMember(power, "Amount") is int a ? a : (int?)null;

    private static void StampInstance(ReplayLine line, object? power)
    {
        if (power == null) return;
        var instancing = Reflect.GetMember(power, "InstanceType")?.ToString();
        if (instancing == null || instancing == "None") return;
        line.Set("inst", instancing).Set("pid", PowerInstances.Of(power));
    }

    private sealed class Intent
    {
        public int Amount;
        public string Src = "effect";
        public int? SrcCid;
        public string? Card;
    }

    private static readonly ConditionalWeakTable<object, Intent> Intents = new();

    private static int? IntendedAmount(object? power, string src)
    {
        if (power == null) return null;
        return Intents.TryGetValue(power, out var intent) && intent.Src == src
            ? intent.Amount : (int?)null;
    }

    private static Type? _combatManagerType;

    private static object? PostCombat()
    {
        var mgr = Reflect.GetStatic(_combatManagerType, "Instance");
        if (mgr == null) return null;
        return Reflect.GetMember(mgr, "IsInProgress") is false ? (object)true : null;
    }

    private static int? HpOf(object? creature, string name)
        => Reflect.GetMember(creature, name) is int v ? v : (int?)null;

    private static bool? MineCreature(object? creature)
        => Reflect.GetMember(creature, "IsPlayer") is true
            ? Mine(Reflect.GetMember(creature, "Player"))
            : (bool?)null;

    private static void Healed(object __0, decimal __1)
    {
        try
        {
            if (Reflect.GetMember(__0, "IsPlayer") is not true
                && Reflect.GetMember(
                       Reflect.GetStatic(_combatManagerType, "Instance"), "IsEnding") is true)
                return;
            if (__1 <= 0m) return;
            if (HpOf(__0, "CurrentHp") is not int oldHp) return;
            if (HpOf(__0, "MaxHp") is not int max) return;
            var newHp = (int)Math.Min(oldHp + __1, max);
            var d = newHp - oldHp;
            var wasted = (int)Math.Min(__1, 999999999m) - d;
            ReplayRecorder.Line("hp")
                ?.Set("dst", CreatureRef(__0))
                .Set("dst_cid", CreatureSlots.Maybe(__0))
                .Set("d", d)
                .Set("hp", newHp)
                .Set("src", "heal")
                .Set("overheal", wasted > 0 ? wasted : (int?)null)
                .Set("mine", MineCreature(__0))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private sealed class AttackFrame
    {
        public object? Command, Attacker, Card, Combat;
        public long? Id;
        public int? Planned;
    }

    private sealed class DamageFrame
    {
        public object? Target, Dealer, Card, Combat;
        public List<string>? Mods, HpMods;
    }

    private sealed class BlockFrame
    {
        public object? Combat, Card;
        public decimal Amount;
        public List<string>? Mods;
    }

    private static readonly List<AttackFrame> OpenAttacks = new();
    private static readonly List<DamageFrame> OpenDamage = new();
    private static readonly List<BlockFrame> OpenBlock = new();
    private static readonly Stack<List<string>?> PendingDamageMods = new();

    private static long _atkNext;
    private static long _atkHigh;

    private static void ResetAttacks()
    {
        OpenAttacks.Clear();
        OpenDamage.Clear();
        OpenBlock.Clear();
        PendingDamageMods.Clear();
        _atkNext = 0;
    }

    private static long? NextAttackId()
    {
        if (_atkNext <= 0)
        {
            var path = ReplayRecorder.CurrentPath;
            if (path == null) return null;
            _atkNext = Math.Max(Math.Max(ReplayJournal.LastSequence(path) + 1, _atkHigh + 1), 1);
        }
        var id = _atkNext++;
        _atkHigh = Math.Max(_atkHigh, id);
        return id;
    }

    private static List<string>? ModelIds(object? models)
    {
        var ids = Enumerate(models)
            .Select(m => Ids.Bare(Reflect.GetString(m, "Id")) ?? "unknown")
            .Distinct().ToList();
        return ids.Count > 0 ? ids : null;
    }

    private static void AttackOpened(object __0, object __1)
    {
        try
        {
            if (__1 == null) return;
            OpenAttacks.RemoveAll(f => !ReferenceEquals(f.Combat, __0));
            if (OpenAttacks.Count > 16) OpenAttacks.Clear();
            OpenAttacks.Add(new AttackFrame
            {
                Command = __1,
                Attacker = Reflect.GetMember(__1, "Attacker"),
                Card = Reflect.GetMember(__1, "ModelSource"),
                Combat = __0,
            });
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void AttackHitCount(object __1, decimal __result)
    {
        try
        {
            var f = OpenAttacks.LastOrDefault(x => ReferenceEquals(x.Command, __1));
            if (f != null) f.Planned = (int)Math.Ceiling(__result);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static long? AttackIdFor(object combat, object? dealer, object? card, object? props)
    {
        if (OpenAttacks.Count == 0) return null;
        var f = OpenAttacks[^1];
        if (!ReferenceEquals(f.Combat, combat) || !ReferenceEquals(f.Attacker, dealer)
            || !ReferenceEquals(f.Card, card)) return null;
        if (DamageProps(props) is not { } flags || !flags.Contains("move")) return null;
        return f.Id ??= NextAttackId();
    }

    private static void AttackClosed(object __2)
    {
        try
        {
            var at = OpenAttacks.FindLastIndex(x => ReferenceEquals(x.Command, __2));
            if (at < 0) return;
            var f = OpenAttacks[at];
            OpenAttacks.RemoveRange(at, OpenAttacks.Count - at);

            var hits = Enumerate(Reflect.GetMember(__2, "Results")).Count();
            if (hits == 0) return;
            var random = Reflect.GetMember(__2, "IsRandomlyTargeted") is true;
            var shortOfPlan = f.Planned is int planned && planned != hits;
            if (hits == 1 && !random && !shortOfPlan) return;

            ReplayRecorder.Line("attack")
                ?.Set("atk", f.Id)
                .Set("src", CreatureRef(f.Attacker))
                .Set("src_cid", CreatureSlots.Maybe(f.Attacker))
                .Set("card", f.Card == null ? null : Ids.Bare(Reflect.GetString(f.Card, "Id")))
                .Set("hits", hits)
                .Set("hits_planned", shortOfPlan ? f.Planned : null)
                .Set("random", random ? true : (bool?)null)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void DamageModified(object __3)
    {
        try
        {
            if (PendingDamageMods.Count > 16) PendingDamageMods.Clear();
            PendingDamageMods.Push(ModelIds(__3));
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void DamageLanding(object __2, object __3, object __6, object __7)
    {
        try
        {
            var mods = PendingDamageMods.Count > 0 ? PendingDamageMods.Pop() : null;
            if (__2 == null || __3 == null) return;
            OpenDamage.RemoveAll(f => !ReferenceEquals(f.Combat, __2));
            if (OpenDamage.Count > 32) OpenDamage.Clear();
            OpenDamage.Add(new DamageFrame
            {
                Target = __3, Dealer = __6, Card = __7, Combat = __2, Mods = mods,
            });
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void HpLossModified(object __1, object __2)
    {
        try
        {
            if (__1 == null || OpenDamage.Count == 0) return;
            var f = OpenDamage[^1];
            if (!ReferenceEquals(f.Combat, __1)) return;
            if (ModelIds(__2) is not { } ids) return;
            f.HpMods = f.HpMods == null ? ids : f.HpMods.Union(ids).ToList();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static DamageFrame? TakeDamageFrame(object combat, object target, object? dealer, object? card)
    {
        var at = OpenDamage.FindLastIndex(x => ReferenceEquals(x.Target, target));
        if (at < 0) return null;
        var f = OpenDamage[at];
        if (!ReferenceEquals(f.Combat, combat) || !ReferenceEquals(f.Dealer, dealer)
            || !ReferenceEquals(f.Card, card)) return null;
        OpenDamage.RemoveAt(at);
        return f;
    }

    private static void BlockModified(object __0, decimal __1, object __2, object __4)
    {
        try
        {
            OpenBlock.RemoveAll(f => !ReferenceEquals(f.Combat, __0));
            if (OpenBlock.Count > 16) OpenBlock.Clear();
            OpenBlock.Add(new BlockFrame { Combat = __0, Card = __2, Amount = __1, Mods = ModelIds(__4) });
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static List<string>? TakeBlockMods(object combat, decimal amount, object? card)
    {
        var at = OpenBlock.FindLastIndex(x => ReferenceEquals(x.Combat, combat)
                                              && x.Amount == amount && ReferenceEquals(x.Card, card));
        if (at < 0) return null;
        var mods = OpenBlock[at].Mods;
        OpenBlock.RemoveRange(at, OpenBlock.Count - at);
        return mods;
    }

    private static readonly ConditionalWeakTable<object, object> BlockBeforeClear = new();

    private static void BlockGained(object __0, object __1, decimal __2, object __4)
    {
        try
        {
            var mods = TakeBlockMods(__0, __2, __4);

            BlockRow(__1, (int)__2, "gained")
                ?.Set("card", __4 == null ? null : Ids.Bare(Reflect.GetString(__4, "Id")))
                .Set("mods", mods)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void BlockClearing(object __instance)
    {
        try
        {
            if (__instance == null) return;
            BlockBeforeClear.Remove(__instance);
            if (IntOf(__instance, "Block") is int block && block > 0)
                BlockBeforeClear.Add(__instance, block);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void BlockCleared(object __1)
    {
        try
        {
            if (__1 == null) return;
            if (!BlockBeforeClear.TryGetValue(__1, out var latched)) return;
            BlockBeforeClear.Remove(__1);
            if (latched is not int had || had <= 0) return;
            if (IntOf(__1, "Block") != 0) return;
            BlockRow(__1, -had, "cleared").Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void BlockClearPrevented(object __1, object __2)
    {
        try
        {
            if (__2 == null) return;
            BlockBeforeClear.Remove(__2);
            BlockRow(__2, null, "prevented")
                ?.Set("by", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void BlockBroken(object __2)
    {
        try
        {
            if (__2 == null) return;
            BlockRow(__2, null, "broken").Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void BlockLost(object __instance, decimal __0)
    {
        try
        {
            if (__instance == null || __0 <= 0m) return;
            if (IntOf(__instance, "Block") is not int block || block <= 0) return;
            var removed = (int)Math.Min(__0, block);
            if (removed <= 0) return;
            BlockRow(__instance, -removed, "lost", block - removed).Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static ReplayLine? BlockRow(object creature, int? n, string reason, int? left = null)
        => ReplayRecorder.Line("block")
            ?.Set("src", CreatureRef(creature))
            .Set("src_cid", CreatureSlots.Maybe(creature))
            .Set("n", n)
            .Set("left", left ?? IntOf(creature, "Block"))
            .Set("reason", reason);

    private static void HpChanged(object __1, object __2, decimal __3)
    {
        try
        {
            var d = (int)__3;
            if (d >= 0) return;
            if (__1 != null) return;
            ReplayRecorder.Line("hp")
                ?.Set("dst", CreatureRef(__2))
                .Set("dst_cid", CreatureSlots.Maybe(__2))
                .Set("d", d)
                .Set("hp", HpOf(__2, "CurrentHp"))
                .Set("src", "loss")
                .Set("mine", MineCreature(__2))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void MaxHpSet(object __0, decimal __1)
    {
        try
        {
            if (HpOf(__0, "MaxHp") is not int oldMax) return;
            var newMax = (int)Math.Min(Math.Max(0m, __1), 999999999m);
            if (newMax == oldMax) return;
            if (ReplayRecorder.Line("max_hp") is not { } line) return;
            line.Set("dst", CreatureRef(__0))
                .Set("dst_cid", CreatureSlots.Maybe(__0))
                .Set("d", newMax - oldMax)
                .Set("max_hp", newMax);
            if (HpOf(__0, "CurrentHp") is int cur) line.Set("hp", Math.Min(cur, newMax));
            line.Set("mine", MineCreature(__0)).Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CurrentHpSet(object __0, decimal __1)
    {
        try
        {
            if (HpOf(__0, "CurrentHp") is not int oldHp) return;
            if (HpOf(__0, "MaxHp") is not int max) return;
            var newHp = (int)Math.Min(__1, max);
            if (newHp == oldHp) return;
            ReplayRecorder.Line("hp")
                ?.Set("dst", CreatureRef(__0))
                .Set("dst_cid", CreatureSlots.Maybe(__0))
                .Set("d", newHp - oldHp)
                .Set("hp", newHp)
                .Set("src", "set")
                .Set("mine", MineCreature(__0))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void GoldGained(object __1)
    {
        try
        {
            ReplayRecorder.Line("gold")
                ?.Set("gold", Reflect.GetInt(__1, "Gold", 0))
                .Set("mine", Mine(__1))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static int? _goldBefore;
    private static bool _goldShop;
    private static Type? _merchantEntryType;
    private static Type? _cardModelType;

    private static void GoldLosing(object __1)
    {
        _goldBefore = null;
        _goldShop = false;
        try
        {
            _goldBefore = IntOf(__1, "Gold");
            _goldShop = PaidByMerchant();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void GoldLost(object __1, object __2)
    {
        try
        {
            if (_goldBefore is not int before) return;
            _goldBefore = null;
            if (_goldShop) return;
            if (IntOf(__1, "Gold") is not int after || after == before) return;
            ReplayRecorder.Line("gold")
                ?.Set("gold", after)
                .Set("d", after - before)
                .Set("src", __2?.ToString()?.ToLowerInvariant())
                .Set("mine", Mine(__1))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static bool PaidByMerchant()
    {
        var owner = CallerOf("MegaCrit.Sts2.Core.Commands.PlayerCmd", out var body);
        if (owner == null) return false;
        if (_merchantEntryType != null && _merchantEntryType.IsAssignableFrom(owner)) return true;
        return owner.Name == "OneOffSynchronizer" && body.StartsWith("<DoMerchantCardRemoval>");
    }

    private static Type? CallerOf(string self, out string body)
    {
        body = "";
        foreach (var frame in new System.Diagnostics.StackTrace(1, false).GetFrames())
        {
            MethodBase? m;
            try { m = Harmony.GetMethodFromStackframe(frame); }
            catch { m = frame.GetMethod(); }
            var t = m?.DeclaringType;
            if (t == null) continue;
            var owner = t.Name.StartsWith("<") && t.DeclaringType != null ? t.DeclaringType : t;
            if (owner == typeof(ReplayHooks) || owner.FullName == self
                || (owner.Namespace?.StartsWith("System") ?? false)) continue;
            body = t.Name;
            return owner;
        }
        return null;
    }

    private static string? _energyTurnMark;
    private static bool? _energyTurnCarry;
    private static int? _energyTurnFrom;
    private static bool _cardPayingEnergy;
    private static bool _cardPayingStars;

    private static void EnergyResetting() { _energyTurnMark = "reset"; }
    private static void EnergyCarrying() { _energyTurnMark = "carry"; }

    private static readonly ConditionalWeakTable<object, object> PaidByPlay = new();

    private static void CardSpending(object __instance)
    {
        try
        {
            PaidByPlay.Remove(__instance);
            if (CallerOf("MegaCrit.Sts2.Core.Models.CardModel", out _)?.Name == "PlayCardAction")
                PaidByPlay.Add(__instance, true);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CardEnergyPaying(object __instance, int __0)
        => _cardPayingEnergy = __0 > 0 && PaidByPlay.TryGetValue(__instance, out _);
    private static void CardEnergyPaid() => _cardPayingEnergy = false;
    private static void CardStarsPaying(object __instance, int __0)
    {
        _cardPayingStars = __0 > 0 && PaidByPlay.TryGetValue(__instance, out _);
        PaidByPlay.Remove(__instance);
    }
    private static void CardStarsPaid() => _cardPayingStars = false;

    private static void EnergySet(object __instance, int __0)
    {
        try
        {
            if (_energyTurnMark is { } mark)
            {
                _energyTurnMark = null;
                _energyTurnCarry = mark == "carry";
                _energyTurnFrom = IntOf(__instance, "Energy");
                return;
            }
            if (_cardPayingEnergy) return;
            if (IntOf(__instance, "Energy") is not int old || old == __0) return;
            ReplayRecorder.Line("energy")
                ?.Set("d", __0 - old)
                .Set("energy", __0)
                .Set("mine", Mine(Reflect.GetMember(__instance, "_player")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void EnergyReset(object __1)
    {
        try
        {
            var carry = _energyTurnCarry;
            var from = _energyTurnFrom;
            _energyTurnCarry = null;
            _energyTurnFrom = null;
            var state = Reflect.GetMember(__1, "PlayerCombatState");
            if (IntOf(state, "Energy") is not int energy) return;
            ReplayRecorder.Line("energy")
                ?.Set("src", "turn")
                .Set("n", TurnNumberOf(__1))
                .Set("d", from is int f ? energy - f : (int?)null)
                .Set("energy", energy)
                .Set("max", IntOf(state, "MaxEnergy"))
                .Set("carry", carry)
                .Set("mine", Mine(__1))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void StarsModified(int __1, object __2)
    {
        try
        {
            if (_cardPayingStars) return;
            if (IntOf(Reflect.GetMember(__2, "PlayerCombatState"), "Stars") is not int stars) return;
            ReplayRecorder.Line("stars")
                ?.Set("d", __1)
                .Set("stars", stars)
                .Set("mine", Mine(__2))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void Forged(decimal __1, object __2, object? __3)
    {
        try
        {
            if (ReplayRecorder.Line("forge") is not { } line) return;
            line.Set("amount", (int)__1)
                .Set("src", Ids.Bare(Reflect.GetString(__3, "Id")))
                .Set("src_c", _cardModelType?.IsInstanceOfType(__3) == true
                    ? CardInstances.Of(__3) : (int?)null);
            var blades = new List<ReplayLine>();
            if (Reflect.GetMember(Reflect.GetMember(__2, "PlayerCombatState"), "AllCards") is IEnumerable all)
            {
                foreach (var card in all)
                {
                    if (card?.GetType().Name != "SovereignBlade" || Reflect.GetMember(card, "IsDupe") is true)
                        continue;
                    var dmg = Reflect.GetMember(Reflect.GetMember(Reflect.GetMember(card, "DynamicVars"),
                        "Damage"), "BaseValue") is decimal d ? (int)d : (int?)null;
                    blades.Add(new ReplayLine("blade").Set("c", CardInstances.Of(card)).Set("dmg", dmg));
                }
                line.Set("blades", blades);
            }
            line.Set("mine", Mine(__2)).Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static readonly ConditionalWeakTable<object, string> OrbIds = new();
    private static int _nextOrb;
    private static string OrbId(object orb)
        => OrbIds.GetValue(orb, _ => $"{ReplayRecorder.AttemptId}.{System.Threading.Interlocked.Increment(ref _nextOrb)}");

    private sealed class EvokeLatch
    {
        public int? Val;
        public bool Dequeue;
    }
    private static readonly ConditionalWeakTable<object, EvokeLatch> Evoking = new();

    private static void OrbChanneled(object __3) => Orb("orb_channel", __3)?.Emit();

    private static void OrbEvoked(object __2)
    {
        try
        {
            var line = Orb("orb_evoke", __2);
            if (line != null && Evoking.TryGetValue(__2, out var latch))
            {
                Evoking.Remove(__2);
                line.Set("val", latch.Val)
                    .Set("kept", !latch.Dequeue);
            }
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void OrbEvoking(object __2, bool __3)
    {
        try
        {
            Evoking.AddOrUpdate(__2, new EvokeLatch { Val = OrbValue(__2, "EvokeVal"), Dequeue = __3 });
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void OrbPassive(object __instance)
    {
        try
        {
            Orb("orb_passive", __instance)?.Set("val", OrbValue(__instance, "PassiveVal")).Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static ReplayLine? Orb(string kind, object orb)
    {
        try
        {
            return ReplayRecorder.Line(kind)
                ?.Set("id", Ids.Bare(Reflect.GetString(orb, "Id")))
                .Set("oid", orb == null ? null : OrbId(orb))
                .Set("mine", Mine(Reflect.GetMember(orb, "Owner")));
        }
        catch { return null; }
    }

    private static int? OrbValue(object orb, string name)
        => Reflect.GetMember(orb, name) is decimal d ? (int)d : (int?)null;

    private static IEnumerable<Type> OrbTypesWithPassive(Type? orbModel)
    {
        if (orbModel == null) return Array.Empty<Type>();
        Type[] types;
        try { types = orbModel.Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
        return types.Where(t => !t.IsAbstract && orbModel.IsAssignableFrom(t)
                                && t.GetMethod("Passive", BindingFlags.Public | BindingFlags.Instance
                                                          | BindingFlags.DeclaredOnly) != null);
    }

    private static void CardRewardPopulated(object __instance)
    {
        try
        {
            if (ReplayRecorder.Line("decision") is not { } line) return;
            var generation = 0;
            if (_pendingReroll > 0 && _decision > 0)
            {
                generation = _pendingReroll;
            }
            else
            {
                DemoteDecision();
                _decision = ReplayRecorder.NextDecisionId();
            }
            _pendingReroll = 0;
            _decisionType = "card_reward";
            _offerIndex = new Dictionary<string, int>();
            _ambiguousOffers = new HashSet<string>();

            var options = new List<ReplayLine>();
            var i = 0;
            foreach (var card in Enumerate(Reflect.GetMember(__instance, "Cards")))
            {
                var offerId = Ids.Bare(Reflect.GetString(card, "Id")) ?? "";
                if (!(_offerIndex ??= new Dictionary<string, int>()).TryAdd(offerId, i))
                    (_ambiguousOffers ??= new HashSet<string>()).Add(offerId);
                options.Add(new ReplayLine("o")
                    .Set("option_index", i++)
                    .Set("option_kind", "card")
                    .Set("option_id", Ids.Bare(Reflect.GetString(card, "Id")))
                    .Set("instance_id", CardInstances.Of(card))
                    .Set("up", Reflect.GetInt(card, "CurrentUpgradeLevel", 0))
                    .SetFlag("presented", true)
                    .SetFlag("selectable", true));
            }

            line.Set("decision_id", _decision)
                .Set("decision_type", _decisionType)
                .Set("source", "reward")
                .Set("offer_generation", generation)
                .Set("n_presented", options.Count)
                .Set("n_selectable", options.Count)
                .SetFlag("decline_available", Reflect.GetBool(__instance, "CanSkip", true))
                .SetFlag("can_reroll", Reflect.GetBool(__instance, "CanReroll"))
                .Set("options", options)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CardRewardSkipped()
    {
        try
        {
            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", _decision)
                .Set("decision_type", _decisionType)
                .Set("outcome", "skip")
                .Emit();
            DemoteDecision();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CardRewardRerolled()
    {
        try
        {
            _pendingReroll++;
            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", _decision > 0 ? _decision : (int?)null)
                .Set("decision_type", _decisionType)
                .Set("outcome", "reroll")
                .Set("offer_generation", _pendingReroll - 1)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static string SelectKind(object? prefs)
    {
        var key = Reflect.GetString(Reflect.GetMember(prefs, "Prompt"), "LocEntryKey") ?? "";
        if (key.Contains("REMOVE")) return "remove";
        if (key.Contains("UPGRADE")) return "upgrade";
        if (key.Contains("TRANSFORM")) return "transform";
        if (key.Contains("EXHAUST")) return "exhaust";
        if (key.Contains("ENCHANT")) return "enchant";
        if (key.Contains("DISCARD")) return "discard";
        return "unknown";
    }

    private static void DeckSelectOffered(object __0, object __1, object __2)
    {
        try
        {
            var deck = Reflect.GetMember(Reflect.GetMember(__0, "Deck"), "Cards");
            var kind = _pendingDeckKind ?? SelectKind(__1);
            _pendingDeckKind = null;
            OpenSelectOffer("deck_select", kind, __0, __1, Enumerate(deck), __2)
                ?.Set("prompt_key", kind == "unknown" ? PromptKey(__1) : null)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void EnchantSelectOffered(object __0, object __1, int __2, object __3)
    {
        try
        {
            var cards = new List<object>(Enumerate(__0));
            if (cards.Count == 0) return;

            OpenSelectOffer("deck_select", SelectKind(__3), Reflect.GetMember(cards[0], "Owner"),
                            __3, cards, filter: null)
                ?.Set("enchantment", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Set("amount", __2)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static ReplayLine? OpenSelectOffer(string source, string kind, object? player,
                                               object? prefs, IEnumerable<object> cards,
                                               object? filter)
    {
        if (ReplayRecorder.Line("decision") is not { } line) return null;
        FlushSelectOutcome();
        _decision = ReplayRecorder.NextDecisionId();
        _decisionType = source + "_" + kind;

        var selectableCount = 0;
        _selectByInstance = new Dictionary<int, int>();
        _selected = new List<int>();
        _selectDecision = _decision;
        _selectDecisionType = _decisionType;

        var options = new List<ReplayLine>();
        var i = 0;
        foreach (var card in cards)
        {
            var id = CardInstances.Of(card);
            var selectable = filter == null
                || Reflect.CallWith(filter, "Invoke", card) is true;
            if (selectable) selectableCount++;
            if (id != 0) _selectByInstance[id] = i;
            var row = new ReplayLine("o")
                .Set("option_index", i++)
                .Set("option_kind", kind)
                .Set("option_id", Ids.Bare(Reflect.GetString(card, "Id")))
                .Set("instance_id", id)
                .Set("up", Reflect.GetInt(card, "CurrentUpgradeLevel", 0))
                .SetFlag("presented", true)
                .SetFlag("selectable", selectable);
            if (!selectable)
                row.Set("selectable_reason",
                    Reflect.GetBool(card, "IsRemovable", true) ? "filtered" : "eternal");
            options.Add(row);
        }

        return line.Set("decision_id", _decision)
            .Set("decision_type", _decisionType)
            .Set("source", source)
            .Set("select_kind", kind)
            .Set("min_select", Reflect.GetInt(prefs, "MinSelect", 1))
            .Set("max_select", Reflect.GetInt(prefs, "MaxSelect", 1))
            .SetFlag("decline_available", Reflect.GetBool(prefs, "Cancelable"))
            .Set("n_presented", options.Count)
            .Set("n_selectable", selectableCount)
            .Set("gold_on_hand", Reflect.GetInt(player, "Gold", 0))
            .Set("options", options);
    }

    private static void SelectionReturned(object __0, object __1)
    {
        try
        {
            var picked = new List<ReplayLine>();
            var joined = false;
            foreach (var card in Enumerate(__1))
            {
                var optionIndex = SelectIndexOf(card);
                if (optionIndex != null) joined = true;
                picked.Add(new ReplayLine("p")
                    .Set("option_index", optionIndex)
                    .Set("c", CardInstances.Of(card))
                    .Set("id", Ids.Bare(Reflect.GetString(card, "Id")))
                    .Set("up", Reflect.GetInt(card, "CurrentUpgradeLevel", 0)));
            }

            ReplayRecorder.Line("pick")
                ?.Set("decision_id", joined ? _selectDecision : (int?)null)
                .Set("decision_type", joined ? _selectDecisionType : null)
                .Set("n_picked", picked.Count)
                .Set("selector", SelectorName())
                .Set("mine", Mine(__0))
                .Set("cards", picked)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static string? SelectorName()
    {
        if (_selectorProp == null) return null;
        var selector = Reflect.Read(_selectorProp);
        return selector == null ? "human" : selector.GetType().Name;
    }

    private static bool? Mine(object? player)
        => LocalPlayer.IsCoop ? LocalPlayer.IsLocalPlayer(player) : (bool?)null;

    private static void CardRemoved(object __1)
    {
        try
        {
            ReplayRecorder.Line("remove")
                ?.Set("decision_id", _decision > 0 ? _decision : (int?)null)
                .Set("option_index", SelectIndexOf(__1))
                .Set("c", CardInstances.Of(__1))
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void ItemPurchased(object __1, object __2, int __3)
    {
        try
        {
            var kind = _pendingBuyKind
                ?? (__2.GetType().Name.Contains("CardRemoval") ? "removal_service" : "other");
            var id = _pendingBuyId;
            _pendingBuyId = null;
            _pendingBuyKind = null;

            ReplayRecorder.Line("buy")
                ?.Set("decision_id",
                    kind == "removal_service" && _decision > 0 ? _decision : (int?)null)
                .Set("kind", kind)
                .Set("slot", ShopSlot(__1, __2, kind))
                .Set("id", id)
                .Set("cost_current", __3)
                .Set("cost_resource", "gold")
                .Set("gold_on_hand", Reflect.GetInt(__1, "Gold", 0))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static int? ShopSlot(object player, object entry, string kind)
    {
        try
        {
            if (kind == "removal_service") return null;
            var room = Reflect.GetMember(Reflect.GetMember(player, "RunState"), "CurrentRoom");
            var inv = Reflect.Call(room, "GetLocalInventory");
            if (inv == null) return null;

            var lists = kind switch
            {
                "card" => new[] { "CharacterCardEntries", "ColorlessCardEntries" },
                "relic" => new[] { "RelicEntries" },
                "potion" => new[] { "PotionEntries" },
                _ => Array.Empty<string>(),
            };
            var i = 0;
            foreach (var listName in lists)
            {
                foreach (var e in Enumerate(Reflect.GetMember(inv, listName)))
                {
                    if (ReferenceEquals(e, entry)) return i;
                    i++;
                }
            }
        }
        catch { }
        return null;
    }

    private static void RewardTaken(object __2)
    {
        try
        {
            var kind = __2.GetType().Name;
            var line = ReplayRecorder.Line("resolve");
            if (line == null) return;
            line.Set("decision_id", kind == "RelicReward" ? RelicRewardDecision(__2, mint: false)
                                    : _decision > 0 ? _decision : (int?)null)
                .Set("reward_kind", kind);
            switch (kind)
            {
                case "PotionReward":
                    line.Set("id", Ids.Bare(Reflect.GetString(Reflect.GetMember(__2, "Potion"), "Id")));
                    break;
                case "GoldReward":
                    line.Set("gold", Reflect.GetInt(__2, "Amount", 0));
                    break;
                case "RelicReward":
                    line.Set("id", Ids.Bare(Reflect.GetString(Reflect.GetMember(__2, "Relic"), "Id")));
                    break;
            }
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CardChangedPiles(object __0, object __1, object __2, object __3)
    {
        try
        {
            if (__1 != null) return;
            if (__3?.ToString() != "None") return;
            var room = Reflect.GetMember(__0, "CurrentRoom");
            if (room == null) return;

            var acquiredId = Ids.Bare(Reflect.GetString(__2, "Id"));
            int? optionIndex = null;
            var decisionForCard = 0;
            if (acquiredId != null && _ambiguousOffers?.Contains(acquiredId) != true)
            {
                if (_offerIndex != null && _offerIndex.TryGetValue(acquiredId, out var oi))
                { optionIndex = oi; decisionForCard = _decision; }
                else if (_prevOfferIndex != null && _prevOfferIndex.TryGetValue(acquiredId, out var po))
                { optionIndex = po; decisionForCard = _prevDecision; }
            }
            var offered = optionIndex != null;
            var roomName = room.GetType().Name;
            var source = offered ? "reward"
                : roomName == "MerchantRoom" ? "shop"
                : roomName == "EventRoom" ? "event"
                : "granted";

            ReplayRecorder.Line("acquire")
                ?.Set("decision_id", offered ? decisionForCard
                        : source == "granted" || source == "shop" ? (int?)null
                        : _decisionType == "deck_select_remove"
                          || _decisionType == EnchantSelectType ? (int?)null
                        : _decision > 0 ? _decision : (int?)null)
                .Set("source", source)
                .Set("c", CardInstances.Of(__2))
                .Set("id", acquiredId)
                .Set("option_index", optionIndex)
                .Emit();
            if (offered && decisionForCard == _decision) DemoteDecision();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void UpgradesStarting(object __0, out List<(object Card, int Level)>? __state)
    {
        __state = null;
        try
        {
            __state = Enumerate(__0).Select(c => (c, Reflect.GetInt(c, "CurrentUpgradeLevel", 0))).ToList();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void UpgradesDone(List<(object Card, int Level)>? __state)
    {
        if (__state == null) return;
        foreach (var (card, before) in __state)
        {
            try
            {
                if (Reflect.GetInt(card, "CurrentUpgradeLevel", 0) > before) CardUpgraded(card);
            }
            catch { }
        }
    }

    private static void CardUpgraded(object __0)
    {
        try
        {
            ReplayRecorder.MarkDeckChanged();
            var offered = _decisionType == UpgradeSelectType;
            ReplayRecorder.Line("upgrade")
                ?.Set("decision_id", _decision > 0 && (offered || _decisionType is "event" or "rest")
                    ? _decision : (int?)null)
                .Set("option_index", offered ? SelectIndexOf(__0) : null)
                .Set("c", CardInstances.Of(__0))
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static int _levelBeforeDowngrade;

    private static void DowngradeStarting(object __0)
    {
        try { _levelBeforeDowngrade = Reflect.GetInt(__0, "CurrentUpgradeLevel", 0); } catch { }
    }

    private static void CardDowngraded(object __0)
    {
        try
        {
            var before = _levelBeforeDowngrade;
            _levelBeforeDowngrade = 0;
            var after = Reflect.GetInt(__0, "CurrentUpgradeLevel", 0);
            if (after >= before) return;
            ReplayRecorder.MarkDeckChanged();
            ReplayRecorder.Line("downgrade")
                ?.Set("decision_id", _decisionType == "event" && _decision > 0 ? _decision : (int?)null)
                .Set("c", CardInstances.Of(__0))
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Set("from_up", before)
                .Set("up", after)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CardEnchanted(object __0, object __1, decimal __2, object? __result)
    {
        try
        {
            if (__result == null) return;

            var offered = _decisionType == EnchantSelectType;
            var optionIndex = offered ? SelectIndexOf(__1) : null;

            ReplayRecorder.MarkDeckChanged();
            ReplayRecorder.Line("enchant")
                ?.Set("decision_id", optionIndex != null ? _decision : (int?)null)
                .Set("option_index", optionIndex)
                .Set("c", CardInstances.Of(__1))
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Set("enchantment", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Set("amount", __2)
                .Set("amount_total", Reflect.GetInt(__result, "Amount", 0))
                .Set("pile", PileName(__1))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static string PileName(object? card)
    {
        var pile = Reflect.GetMember(card, "Pile");
        var type = pile == null ? "None" : Reflect.GetString(pile, "Type");
        return (type ?? "unknown").ToLowerInvariant();
    }

    private static object? _transformFrom;

    private static void TransformedFrom(object __instance)
    {
        _transformFrom = __instance;
    }

    private static void TransformedTo(object __instance)
    {
        var from = _transformFrom;
        _transformFrom = null;
        try
        {
            var offered = _decisionType == TransformSelectType;
            ReplayRecorder.Line("transform")
                ?.Set("decision_id", _decision > 0 && (offered || _decisionType is "event" or "rest")
                    ? _decision : (int?)null)
                .Set("option_index", offered ? SelectIndexOf(from) : null)
                .Set("from_c", from == null ? (int?)null : CardInstances.Of(from))
                .Set("from_id", Ids.Bare(Reflect.GetString(from, "Id")))
                .Set("to_c", CardInstances.Of(__instance))
                .Set("to_id", Ids.Bare(Reflect.GetString(__instance, "Id")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void RelicObtained(object __0)
    {
        try
        {
            var fromShelf = _pendingBuyKind != null;
            var grantable = _decisionType is "event" or "rest"
                || _decisionType == TreasureType && _treasureOfferIds?.Contains(Ids.Bare(Reflect.GetString(__0, "Id")) ?? "") == true;
            ReplayRecorder.Line("relic")
                ?.Set("decision_id",
                      !fromShelf && grantable && _decision > 0 ? _decision : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static bool _replacingRelic;

    private static void RelicReplacing() => _replacingRelic = true;

    private static void RelicRemoved(object __0)
    {
        try
        {
            var replacing = _replacingRelic;
            _replacingRelic = false;
            if (Reflect.GetMember(__0, "HasBeenRemovedFromState") is not true) return;
            ReplayRecorder.Line("relic_lost")
                ?.Set("decision_id",
                      !replacing && _decisionType == "event" && _decision > 0
                          ? _decision : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Set("reason", replacing ? "replaced" : "removed")
                .Set("mine", Mine(Reflect.GetMember(__0, "Owner")))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void PotionUsed(object __2, object __3) => Potion("potion_used", __2, __3);
    private static void PotionStarting(object __2, object __3) => Potion("potion_start", __2, __3);

    private static void ConsoleCommand(object? __0, string __1, string[] __2)
    {
        try
        {
            ReplayRecorder.Line("console")
                ?.Set("cmd", __1)
                .Set("args", __2 is { Length: > 0 } ? __2.ToList() : null)
                .Set("mine", Mine(__0))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }
    private static void PotionProcured(object __2) => Potion("potion_got", __2);
    private static void PotionDiscarded(object __2) => Potion("potion_dropped", __2);

    private static void Potion(string kind, object potion, object? target = null)
    {
        try
        {
            ReplayRecorder.Line(kind)
                ?.Set("id", Ids.Bare(Reflect.GetString(potion, "Id")))
                .Set("target",
                     target == null ? null : Ids.Bare(Reflect.GetString(target, "ModelId")))
                .Set("target_cid", CreatureSlots.Maybe(target))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void RestHeal(bool __2)
    {
        try
        {
            if (_restFunnel && !__2) return;
            ReplayRecorder.Line("rest")
                ?.Set("option", "heal")
                .SetFlag("mimicked", __2)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void RestSmith()
    {
        if (_restFunnel) return;
        try { ReplayRecorder.Line("rest")?.Set("option", "smith").Emit(); } catch { }
    }

    private static bool _restFunnel;

    private static int _restDecision;
    private static int _restChoiceIndex;

    private static MethodInfo? _restOptionsFor;

    private sealed record RestChoice(object Player, string? Id, int Index, bool Mine, int Decision);
    private static RestChoice? _restPending;

    private static void RestSiteEntered(object __1)
    {
        try
        {
            if (__1?.GetType().Name != "RestSiteRoom") return;
            _restChoiceIndex = 0;
            OfferRest(Reflect.GetMember(__1, "Options"));
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void OfferRest(object? list)
    {
        _restDecision = 0;
        var options = new List<ReplayLine>();
        var selectable = 0;
        var enabledKnown = true;
        foreach (var opt in Enumerate(list))
        {
            var row = new ReplayLine("o")
                .Set("option_index", options.Count)
                .Set("option_kind", "rest_option")
                .Set("option_id", Reflect.GetString(opt, "OptionId")?.ToLowerInvariant())
                .SetFlag("presented", true);
            if (Reflect.GetMember(opt, "IsEnabled") is bool enabled)
            {
                row.SetFlag("selectable", enabled);
                if (enabled) selectable++;
                else row.Set("selectable_reason", "disabled");
            }
            else enabledKnown = false;
            options.Add(row);
        }
        if (options.Count == 0) return;
        if (ReplayRecorder.Line("decision") is not { } line) return;
        _restDecision = ReplayRecorder.NextDecisionId();
        line.Set("decision_id", _restDecision)
            .Set("decision_type", "rest")
            .Set("source", "rest_site")
            .Set("choice_index", _restChoiceIndex)
            .SetFlag("decline_available", _restChoiceIndex > 0)
            .Set("n_presented", options.Count)
            .Set("n_selectable", enabledKnown ? selectable : (int?)null)
            .Set("options", options)
            .Emit();
    }

    private static void RestChoosing(object __instance, object __0, int __1)
    {
        _restPending = null;
        try
        {
            object? opt = null;
            try { opt = ElementAt(_restOptionsFor?.Invoke(__instance, new[] { __0 }), __1); }
            catch { }
            var mine = !LocalPlayer.IsCoop || LocalPlayer.IsLocalPlayer(__0);
            var decision = mine ? _restDecision : 0;
            if (decision > 0)
            {
                DemoteDecision();
                _decision = decision;
                _decisionType = "rest";
            }
            _restPending = new RestChoice(__0, Reflect.GetString(opt, "OptionId")?.ToLowerInvariant(),
                                          __1, mine, decision);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void RestChosen(object? __result)
    {
        var choice = _restPending;
        _restPending = null;
        try
        {
            if (choice == null || __result is not System.Threading.Tasks.Task<bool> task) return;
            task.ContinueWith(t => RestChoiceSettled(choice, t),
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                System.Threading.Tasks.TaskScheduler.Default);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void RestChoiceSettled(RestChoice choice, System.Threading.Tasks.Task<bool> t)
    {
        try
        {
            if (choice.Decision > 0 && _decisionType == "rest" && _decision == choice.Decision)
                DemoteDecision();
            if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || !t.Result) return;

            var line = ReplayRecorder.Line("rest");
            line?.Set("option", choice.Id)
                .Set("decision_id", choice.Decision > 0 ? choice.Decision : (int?)null)
                .Set("option_index", choice.Decision > 0 ? choice.Index : (int?)null)
                .Set("mine", Mine(choice.Player));
            if (choice.Id == "heal") line?.SetFlag("mimicked", false);
            line?.Emit();

            if (!choice.Mine) return;
            _restChoiceIndex++;
            OfferRest(Reflect.Call(Reflect.GetStatic(
                HookPatcher.FindType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance") is { } rm
                    ? Reflect.GetMember(rm, "RestSiteSynchronizer") : null, "GetLocalOptions"));
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static readonly ConditionalWeakTable<object, StrongBox<int>> _relicCounters = new();

    private static void RelicCounterChanged(object __instance)
    {
        try
        {
            if (Reflect.GetMember(Reflect.GetStatic(_combatManagerType, "Instance"),
                                  "IsInProgress") is not false) return;
            if (Reflect.GetMember(__instance, "DisplayAmount") is not int n) return;
            var owner = Reflect.GetMember(__instance, "Owner");
            if (owner == null) return;
            if (_relicCounters.TryGetValue(__instance, out var last) && last.Value == n) return;
            if (ReplayRecorder.Line("relic_counter") is not { } line) return;
            _relicCounters.AddOrUpdate(__instance, new StrongBox<int>(n));
            line.Set("id", Ids.Bare(Reflect.GetString(__instance, "Id")))
                .Set("n", n)
                .Set("decision_id", _decisionType is "event" or "rest" && _decision > 0
                                        ? _decision : (int?)null)
                .Set("mine", Mine(owner))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static string? _pendingDeckKind;

    private static void RemovalSelectEntering() => _pendingDeckKind = "remove";
    private static void RemovalSelectLeft() => _pendingDeckKind = null;

    private static string? PromptKey(object? prefs)
        => Reflect.GetString(Reflect.GetMember(prefs, "Prompt"), "LocEntryKey");

    private static void UpgradeSelectOffered(object __0, object __1)
    {
        try
        {
            var cards = DeckCards(__0).Where(c => Reflect.GetMember(c, "IsUpgradable") is true).ToList();
            if (cards.Count == 0) return;
            OpenSelectOffer("deck_select", "upgrade", __0, __1, cards, filter: null)?.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void TransformSelectOffered(object __0, object __1)
    {
        try
        {
            var cards = DeckCards(__0).Where(c =>
                Reflect.GetMember(c, "IsTransformable") is true
                && Reflect.GetMember(c, "Type") is { } t && t.ToString() != "Quest").ToList();
            if (cards.Count == 0) return;
            OpenSelectOffer("deck_select", "transform", __0, __1, cards, filter: null)?.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static IEnumerable<object> DeckCards(object? player)
        => Enumerate(Reflect.GetMember(Reflect.GetMember(player, "Deck"), "Cards"));

    private const string TreasureType = "treasure";
    private static HashSet<string>? _treasureOfferIds;

    private static void TreasureOffered(object __instance)
    {
        try
        {
            var relics = Enumerate(Reflect.GetMember(__instance, "CurrentRelics")).ToList();
            if (relics.Count == 0) return;
            if (ReplayRecorder.Line("decision") is not { } line) return;
            DemoteDecision();
            _decision = ReplayRecorder.NextDecisionId();
            _decisionType = TreasureType;
            _treasureOfferIds = new HashSet<string>();

            var options = new List<ReplayLine>();
            for (var i = 0; i < relics.Count; i++)
            {
                var id = Ids.Bare(Reflect.GetString(relics[i], "Id"));
                if (id != null) _treasureOfferIds.Add(id);
                options.Add(new ReplayLine("o")
                    .Set("option_index", i)
                    .Set("option_kind", "relic")
                    .Set("option_id", id)
                    .SetFlag("presented", true)
                    .SetFlag("selectable", true));
            }
            line.Set("decision_id", _decision)
                .Set("decision_type", TreasureType)
                .Set("source", "treasure")
                .Set("n_presented", options.Count)
                .Set("n_selectable", options.Count)
                .SetFlag("decline_available", true)
                .Set("options", options)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void TreasurePicked(object __instance, object __0, int? __1)
    {
        try
        {
            if (_decisionType != TreasureType) return;
            var relics = Enumerate(Reflect.GetMember(__instance, "CurrentRelics")).ToList();
            if (relics.Count == 0 || __1 >= relics.Count || __1 < 0) return;
            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", _decision)
                .Set("decision_type", TreasureType)
                .Set("outcome", __1 == null ? "skip" : "select")
                .Set("option_id", __1 is int i ? Ids.Bare(Reflect.GetString(relics[i], "Id")) : null)
                .Set("selected_option_indices", __1 is int j ? new List<int> { j } : new List<int>())
                .Set("mine", Mine(__0))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void TreasureEmpty()
    {
        try
        {
            if (ReplayRecorder.Line("decision") is not { } line) return;
            line.Set("decision_id", ReplayRecorder.NextDecisionId())
                .Set("decision_type", TreasureType)
                .Set("source", "treasure")
                .Set("n_presented", 0)
                .Set("n_selectable", 0)
                .SetFlag("decline_available", false)
                .Set("options", new List<ReplayLine>())
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static readonly ConditionalWeakTable<object, StrongBox<int>> _relicRewardDecisions = new();

    private static int? RelicRewardDecision(object reward, bool mint)
    {
        if (_relicRewardDecisions.TryGetValue(reward, out var box)) return box.Value;
        if (!mint) return null;
        var id = Ids.Bare(Reflect.GetString(Reflect.GetMember(reward, "Relic"), "Id"));
        if (id == null) return null;
        if (ReplayRecorder.Line("decision") is not { } line) return null;
        var decision = ReplayRecorder.NextDecisionId();
        _relicRewardDecisions.AddOrUpdate(reward, new StrongBox<int>(decision));
        line.Set("decision_id", decision)
            .Set("decision_type", "relic_reward")
            .Set("source", "reward")
            .Set("n_presented", 1)
            .Set("n_selectable", 1)
            .SetFlag("decline_available", true)
            .Set("options", new List<ReplayLine>
            {
                new ReplayLine("o")
                    .Set("option_index", 0)
                    .Set("option_kind", "relic")
                    .Set("option_id", id)
                    .SetFlag("presented", true)
                    .SetFlag("selectable", true),
            })
            .Emit();
        return decision;
    }

    private static void RelicRewardSelecting(object __instance)
    {
        try { RelicRewardDecision(__instance, mint: true); }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void RelicRewardSkipped(object __instance)
    {
        try
        {
            if (Reflect.GetMember(__instance, "_wasTaken") is not false) return;
            if (RelicRewardDecision(__instance, mint: true) is not { } decision) return;
            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", decision)
                .Set("decision_type", "relic_reward")
                .Set("outcome", "skip")
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static int _eventDecision;

    private static int _eventPageIndex = -1;

    private static string? _eventId;

    private static PropertyInfo? _optLockedProp;
    private static PropertyInfo? _optProceedProp;
    private static MethodInfo? _addVarsMethod;

    private static void EventBegun(object __0)
    {
        try
        {
            if (LocalPlayer.IsCoop && !LocalPlayer.IsLocalPlayer(__0)) return;
            _eventPageIndex = -1;
            _eventDecision = 0;
            _eventId = null;
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void EventPageShown(object __instance)
    {
        try
        {
            var owner = Reflect.GetMember(__instance, "Owner");
            if (LocalPlayer.IsCoop && !LocalPlayer.IsLocalPlayer(owner)) return;

            _eventDecision = 0;
            var pageIndex = ++_eventPageIndex;
            _eventId = Ids.Bare(Reflect.GetString(__instance, "Id"));

            var options = new List<ReplayLine>();
            var index = 0;
            var real = 0;
            var selectable = 0;
            var lockedKnown = true;
            string? pageKey = null;
            var pageKeyAgrees = true;

            foreach (var opt in Enumerate(Reflect.GetMember(__instance, "CurrentOptions")))
            {
                var key = Reflect.GetString(opt, "TextKey");
                var proceed = OptFlag(_optProceedProp, opt);
                var locked = OptFlag(_optLockedProp, opt);
                if (proceed is not true) real++;
                if (locked == null) lockedKnown = false;
                else if (locked == false) selectable++;

                var row = new ReplayLine("o")
                    .Set("option_index", index++)
                    .Set("option_kind", proceed is true ? "proceed" : "event_option")
                    .Set("option_id", key)
                    .Set("label", OptionText(__instance, opt, "Title"))
                    .Set("desc", OptionText(__instance, opt, "Description"))
                    .Set("grants_card", RoomExport.OptionCard(opt))
                    .Set("grants_relic", Ids.Bare(Reflect.GetString(Reflect.GetMember(opt, "Relic"), "Id")))
                    .SetFlag("presented", true);
                if (locked != null)
                {
                    row.SetFlag("selectable", locked == false);
                    if (locked == true) row.Set("selectable_reason", "locked");
                }
                options.Add(row);

                if (PageKeyOf(key) is not { } p) continue;
                if (pageKey == null) pageKey = p;
                else if (pageKey != p) pageKeyAgrees = false;
            }

            if (real == 0) return;

            if (ReplayRecorder.Line("decision") is not { } line) return;
            _eventDecision = ReplayRecorder.NextDecisionId();
            line.Set("decision_id", _eventDecision)
                .Set("decision_type", "event")
                .Set("source", "event")
                .Set("event_id", _eventId)
                .Set("page_index", pageIndex)
                .Set("page_key", pageKeyAgrees ? pageKey : null)
                .Set("n_presented", options.Count)
                .Set("n_selectable", lockedKnown ? selectable : (int?)null)
                .Set("options", options);
            StampEventFloor(line, owner);
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void EventOptionChosen(object __instance, object __0, int __1)
    {
        try
        {
            if (LocalPlayer.IsCoop && !LocalPlayer.IsLocalPlayer(__0)) return;

            var model = Reflect.CallWith(__instance, "GetEventForPlayer", __0);
            var opt = ElementAt(Reflect.GetMember(model, "CurrentOptions"), __1);
            var chosenKey = Reflect.GetString(opt, "TextKey");
            var label = OptionText(model, opt, "Title") ?? chosenKey;

            DemoteDecision();
            if (_eventDecision > 0)
            {
                _decision = _eventDecision;
                _decisionType = "event";
            }

            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", _eventDecision > 0 ? _eventDecision : (int?)null)
                .Set("decision_type", "event")
                .Set("outcome", "chosen")
                .Set("option_index", __1)
                .Set("option_id", chosenKey)
                .Set("label", label)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void EventProceedClicked(object __0)
    {
        try
        {
            if (OptFlag(_optLockedProp, __0) is true) return;
            if (OptFlag(_optProceedProp, __0) is not true) return;
            _eventDecision = 0;
            ReplayRecorder.Line("event_proceed")
                ?.Set("event_id", _eventId)
                .Set("page_index", _eventPageIndex >= 0 ? _eventPageIndex : (int?)null)
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static bool? OptFlag(PropertyInfo? prop, object? opt)
    {
        if (prop == null || opt == null) return null;
        try { return prop.GetValue(opt) as bool?; }
        catch { return null; }
    }

    private static string? OptionText(object? model, object? opt, string member)
    {
        if (_addVarsMethod == null) return null;
        var loc = Reflect.GetMember(opt, member);
        if (loc == null) return null;
        var vars = Reflect.GetMember(model, "DynamicVars");
        if (vars == null) return null;
        try { _addVarsMethod.Invoke(vars, new[] { loc }); }
        catch { return null; }
        var text = Reflect.CallString(loc, "GetFormattedText");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? PageKeyOf(string? textKey)
    {
        if (textKey == null) return null;
        const string pages = ".pages.";
        const string opts = ".options.";
        var start = textKey.IndexOf(pages, StringComparison.Ordinal);
        if (start < 0) return null;
        start += pages.Length;
        var end = textKey.IndexOf(opts, start, StringComparison.Ordinal);
        return end > start ? textKey.Substring(start, end - start) : null;
    }

    private static void StampEventFloor(ReplayLine line, object? owner)
    {
        var runState = Reflect.GetMember(owner, "RunState");
        if (runState == null) return;
        var floor = Reflect.GetInt(runState, "TotalFloor", -1);
        var actIndex = Reflect.GetInt(runState, "CurrentActIndex", -1);
        if (floor < 0 || actIndex < 0) return;
        line.Set("floor", floor).Set("act", actIndex + 1);
    }

    private static ReplayLine CreatureEntry(ReplayLine row, object c)
        => row.Set("cid", CreatureSlots.Maybe(c))
            .Set("id", Ids.Bare(Reflect.GetString(c, "ModelId")))
            .Set("slot", Reflect.GetString(c, "SlotName"))
            .Set("hp", HpOf(c, "CurrentHp"))
            .Set("max_hp", HpOf(c, "MaxHp"));

    private static void CreatureAdded(object __instance, object __0)
    {
        try
        {
            if (Reflect.GetMember(__instance, "IsInProgress") is not true) return;
            if (ReplayRecorder.Line("spawn") is not { } line) return;
            CreatureEntry(line, __0)
                .Set("side", Reflect.GetMember(__0, "Side")?.ToString() switch
                {
                    "Enemy" => "enemy",
                    "Player" => "ally",
                    _ => null,
                })
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void CreatureEscaped(object __0)
    {
        try
        {
            ReplayRecorder.Line("escape")
                ?.Set("tgt", CreatureRef(__0))
                .Set("tgt_cid", CreatureSlots.Maybe(__0))
                .Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static readonly HashSet<object> _doomed = new(ReferenceEqualityComparer.Instance);

    private static void DoomKillStarting(object __0)
    {
        try
        {
            foreach (var c in Enumerate(__0)) _doomed.Add(c);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void DoomKillDone(object __1)
    {
        try
        {
            foreach (var c in Enumerate(__1)) _doomed.Remove(c);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static WeakReference<object>? _intentsShownFor;

    private static readonly ConditionalWeakTable<object, string> LastIntent = new();

    private static readonly Dictionary<Type, MethodInfo?> SingleDamageMethods = new();

    private static bool IntentsShownFor(object? combat)
        => combat != null && _intentsShownFor != null
           && _intentsShownFor.TryGetTarget(out var shown) && ReferenceEquals(shown, combat);

    private static void IntentTurnStarting()
    {
        _intentsShownFor = null;
    }

    private static void IntentsShown(object __0)
    {
        try
        {
            if (__0 == null || IntentsShownFor(__0)) return;
            _intentsShownFor = new WeakReference<object>(__0);
            foreach (var enemy in Enumerate(Reflect.GetMember(__0, "Enemies")))
            {
                var move = Reflect.GetMember(Reflect.GetMember(enemy, "Monster"), "NextMove");
                if (move != null) EmitIntent(enemy, move, "turn_start");
            }
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void IntentsCommitted(object __0, object __1)
    {
        try
        {
            if (__1?.ToString() != "Player") return;
            foreach (var enemy in Enumerate(Reflect.GetMember(__0, "Enemies")))
            {
                var move = Reflect.GetMember(Reflect.GetMember(enemy, "Monster"), "NextMove");
                if (move == null) continue;
                var (dmg, hits) = AttackNumbers(move, enemy);
                var sig = IntentSig(Reflect.GetString(move, "StateId"), dmg, hits);
                if (LastIntent.TryGetValue(enemy, out var last) && last == sig) continue;
                EmitIntent(enemy, move, "turn_end");
            }
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void MoveSetImmediate(object __instance, object __0, object __1)
    {
        try
        {
            if (__0 == null || __1 is not bool force) return;
            var owner = Reflect.GetMember(__instance, "Creature");
            if (owner == null) return;
            if (Reflect.Call(Reflect.GetMember(owner, "CombatState"), "IsLiveCombat") is not true) return;
            var from = Reflect.GetMember(__instance, "NextMove");
            if (!force && Reflect.GetMember(from, "CanTransitionAway") is not true) return;
            if (ReferenceEquals(from, __0)) return;
            MoveOwners.Remove(__0);
            MoveOwners.Add(__0, owner);
            EmitIntent(owner, __0, "set", Reflect.GetString(from, "StateId"));
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void EmitIntent(object owner, object move, string at, string? from = null)
    {
        var id = Reflect.GetString(move, "StateId");
        var (dmg, hits) = AttackNumbers(move, owner);
        LastIntent.AddOrUpdate(owner, IntentSig(id, dmg, hits));
        var intents = IntentTypes(move);
        ReplayRecorder.Line("intent")
            ?.Set("src", CreatureRef(owner))
            .Set("src_cid", CreatureSlots.Maybe(owner))
            .Set("id", id)
            .Set("at", at)
            .Set("from", from)
            .Set("intents", intents.Count > 0 ? intents : null)
            .Set("dmg", dmg)
            .Set("hits", hits)
            .Emit();
    }

    private static string IntentSig(string? id, int? dmg, int? hits) => $"{id}|{dmg}|{hits}";

    private static List<string> IntentTypes(object move)
        => Enumerate(Reflect.GetMember(move, "Intents"))
            .Select(i => Reflect.GetMember(i, "IntentType")?.ToString()?.ToLowerInvariant())
            .Where(x => x != null).Select(x => x!).ToList();

    private static (int? dmg, int? hits) AttackNumbers(object move, object owner)
    {
        object? attack = null;
        MethodInfo? method = null;
        var count = 0;
        foreach (var intent in Enumerate(Reflect.GetMember(move, "Intents")))
        {
            var m = SingleDamageMethod(intent.GetType());
            if (m == null) continue;
            attack = intent;
            method = m;
            count++;
        }
        if (count != 1 || attack == null || method == null) return (null, null);
        int? dmg = null;
        try
        {
            if (method.Invoke(attack, new object?[] { null, owner }) is int d) dmg = d;
        }
        catch { }
        var hits = Reflect.GetMember(attack, "Repeats") is int r ? r : (int?)null;
        return (dmg, hits);
    }

    private static MethodInfo? SingleDamageMethod(Type type)
    {
        if (SingleDamageMethods.TryGetValue(type, out var cached)) return cached;
        MethodInfo? found = null;
        try
        {
            found = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetSingleDamage" && m.ReturnType == typeof(int)
                                     && m.GetParameters().Length == 2);
        }
        catch { }
        SingleDamageMethods[type] = found;
        return found;
    }

    private static string? Coord(object? coord)
    {
        var raw = coord?.ToString();
        if (raw == null) return null;
        var open = raw.IndexOf('(');
        var close = raw.IndexOf(')');
        if (open < 0 || close <= open) return raw;
        return raw.Substring(open + 1, close - open - 1).Replace(" ", "");
    }

    private static object? ElementAt(object? source, int index)
    {
        if (index < 0) return null;
        var i = 0;
        foreach (var item in Enumerate(source))
            if (i++ == index) return item;
        return null;
    }

    private static IEnumerable<object> Enumerate(object? source)
    {
        if (source is not IEnumerable seq) yield break;
        foreach (var item in seq)
            if (item != null) yield return item;
    }
}
