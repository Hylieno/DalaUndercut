using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.Network.Structures;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DalaLenoUndercut;

public sealed partial class Plugin : IDalamudPlugin
{
    private const string CommandName = "/dlundercut";
    private const int MaxMarketSlots = 20;
    private const int UiTimeoutMs = 5000;

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IMarketBoard MarketBoard { get; set; } = null!;
    [PluginService] private static IAddonLifecycle AddonLifecycle { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private readonly Lumina.Excel.ExcelSheet<Item> items;
    private Configuration configuration;

    private bool windowOpen;
    private bool awaitingMarketResult;
    private bool selectedItemIsHq;
    private uint selectedItemId;
    private string selectedItemName = "(en attente)";
    private MarketSnapshot? lastSnapshot;
    private string status = "Ouvre la liste Marchés d'un servant. Le bouton Undercut All apparaîtra à côté.";

    private AutomationState automationState = AutomationState.Idle;
    private DateTime nextActionAt = DateTime.MinValue;
    private DateTime stateStartedAt = DateTime.MinValue;
    private int currentSlot;
    private int processedCount;
    private int changedCount;
    private int skippedCount;
    private int currentOldPrice;
    private uint? pendingSuggestedPrice;

    private enum AutomationState
    {
        Idle,
        OpenSlot,
        WaitContextMenu,
        SelectAdjustPrice,
        WaitRetainerSell,
        OpenCompare,
        WaitMarketWindow,
        WaitMarketOffers,
        CloseMarket,
        WaitMarketClosed,
        ApplyPrice,
        ConfirmPrice,
        WaitSellClosed,
        NextSlot,
        Finished,
        Error,
    }

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        SanitizeConfiguration();

        items = DataManager.GetExcelSheet<Item>();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Ouvre la fenêtre DalaLeno Undercut.",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
        Framework.Update += OnFrameworkUpdate;

        MarketBoard.OfferingsReceived += OnOfferingsReceived;
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellPostSetup);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearchResult", OnItemSearchResultPostSetup);

        Log.Information("DalaLeno Undercut v0.3.5 loaded.");
    }

    public void Dispose()
    {
        StopAutomation("Plugin déchargé.");
        MarketBoard.OfferingsReceived -= OnOfferingsReceived;
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellPostSetup);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", OnItemSearchResultPostSetup);
        Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args) => windowOpen = true;
    private void OpenMainUi() => windowOpen = true;

    private unsafe void OnRetainerSellPostSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AddonRetainerSell*)args.Addon.Address;
            if (addon == null || addon->ItemName == null)
                return;

            var rawText = addon->ItemName->NodeText.ToString();
            selectedItemIsHq = rawText.Contains('\uE03C');
            selectedItemId = 0;
            selectedItemName = "(en attente du Market Board)";
            lastSnapshot = null;
            currentOldPrice = addon->AskingPrice != null ? addon->AskingPrice->Value : 0;

            if (!IsAutomationRunning)
                status = $"Fenêtre de prix ouverte ({(selectedItemIsHq ? "HQ" : "NQ")}). Clique sur Comparer les prix.";

            if (configuration.OpenDiagnosticWindowAutomatically)
                windowOpen = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to inspect RetainerSell addon.");
            status = "Impossible de lire l'état HQ/NQ. Consulte /xllog.";
        }
    }

    private void OnItemSearchResultPostSetup(AddonEvent type, AddonArgs args)
    {
        if (!IsRetainerContext())
            return;

        awaitingMarketResult = true;
        if (automationState == AutomationState.WaitMarketWindow)
            Transition(AutomationState.WaitMarketOffers, "Market Board ouvert; attente des offres live...");
        else if (!IsAutomationRunning)
            status = "Résultat Market Board ouvert; attente des offres live du jeu...";
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        if (!awaitingMarketResult || !IsRetainerContext())
            return;

        awaitingMarketResult = false;
        pendingSuggestedPrice = null;

        if (offerings.ItemListings.Count == 0)
        {
            lastSnapshot = null;
            status = "Aucune offre reçue pour cet objet.";
            if (automationState == AutomationState.WaitMarketOffers)
                Transition(AutomationState.CloseMarket, "Aucune offre : prix inchangé.");
            return;
        }

        var first = offerings.ItemListings[0];
        selectedItemId = first.ItemId;

        var row = items.GetRowOrDefault(selectedItemId);
        selectedItemName = row?.Name.ToString() ?? $"Item #{selectedItemId}";

        var canBeHq = row?.CanBeHq ?? false;
        var wantedHq = canBeHq && selectedItemIsHq;

        var candidates = offerings.ItemListings
            .Where(x => x.IsHq == wantedHq)
            .Where(x => !configuration.IgnoreOwnRetainers || !IsOwnRetainer(x.RetainerId))
            .OrderBy(x => x.PricePerUnit)
            .ToList();

        if (candidates.Count == 0)
        {
            lastSnapshot = new MarketSnapshot(selectedItemId, selectedItemName, wantedHq, null, null, 0, DateTime.Now);
            status = configuration.IgnoreOwnRetainers
                ? "Aucun listing concurrent correspondant à la qualité de l'objet."
                : "Aucun listing correspondant à la qualité de l'objet.";

            if (automationState == AutomationState.WaitMarketOffers)
                Transition(AutomationState.CloseMarket, $"{selectedItemName}: aucun concurrent, prix inchangé.");
            return;
        }

        var lowest = candidates[0].PricePerUnit;
        var suggested = CalculateUndercutPrice(lowest);
        pendingSuggestedPrice = suggested;

        lastSnapshot = new MarketSnapshot(selectedItemId, selectedItemName, wantedHq, lowest, suggested, candidates.Count, DateTime.Now);
        status = $"OK : {selectedItemName} {(wantedHq ? "HQ" : "NQ")} → {suggested:N0} gil.";
        Log.Information("Market snapshot {ItemId} {Quality}: lowest={Lowest}, suggested={Suggested}",
            selectedItemId, wantedHq ? "HQ" : "NQ", lowest, suggested);

        if (automationState == AutomationState.WaitMarketOffers)
            Transition(AutomationState.CloseMarket, $"{selectedItemName}: {lowest:N0} → cible {suggested:N0} gil.");
    }

    private uint CalculateUndercutPrice(uint lowest)
    {
        var result = (long)lowest - configuration.UndercutAmount;
        result = Math.Max(configuration.MinimumPrice, result);
        result = Math.Max(1, result);
        return (uint)Math.Min(uint.MaxValue, result);
    }

    private unsafe bool IsRetainerContext()
    {
        try
        {
            var module = ItemOrderModule.Instance();
            return module != null && module->ActiveRetainerId != 0;
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool IsOwnRetainer(ulong retainerId)
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
            return false;

        for (uint i = 0; i < manager->GetRetainerCount(); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer != null && retainer->RetainerId == retainerId)
                return true;
        }

        return false;
    }

    private bool IsAutomationRunning => automationState is not AutomationState.Idle and not AutomationState.Finished and not AutomationState.Error;

    private void StartAutomation()
    {
        if (!IsRetainerContext() || !IsAddonVisible("RetainerSellList"))
        {
            status = "Ouvre d'abord la fenêtre Marchés du servant.";
            return;
        }

        currentSlot = 0;
        processedCount = 0;
        changedCount = 0;
        skippedCount = 0;
        pendingSuggestedPrice = null;
        awaitingMarketResult = false;
        windowOpen = true;
        Transition(AutomationState.OpenSlot, "Undercut All démarré.", immediate: true);
        Log.Information("Undercut All started with delay={Delay}ms.", configuration.ActionDelayMs);
    }

    private void StopAutomation(string reason)
    {
        automationState = AutomationState.Idle;
        awaitingMarketResult = false;
        pendingSuggestedPrice = null;
        status = reason;
    }

    private void Transition(AutomationState next, string message, bool immediate = false)
    {
        automationState = next;
        stateStartedAt = DateTime.UtcNow;
        nextActionAt = immediate ? DateTime.UtcNow : DateTime.UtcNow.AddMilliseconds(configuration.ActionDelayMs);
        status = message;
        Log.Debug("Automation -> {State}: {Message}", next, message);
    }

    private bool TimedOut() => (DateTime.UtcNow - stateStartedAt).TotalMilliseconds > UiTimeoutMs;

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsAutomationRunning || DateTime.UtcNow < nextActionAt)
            return;

        try
        {
            TickAutomation();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Automation tick failed.");
            automationState = AutomationState.Error;
            status = $"Erreur automation : {ex.Message}";
        }
    }
}
