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

public sealed partial class Plugin
{
    private unsafe void TickAutomation()
    {
        if (!IsRetainerContext())
        {
            automationState = AutomationState.Error;
            status = "Automation arrêtée : contexte servant perdu.";
            return;
        }

        switch (automationState)
        {
            case AutomationState.OpenSlot:
                if (!IsAddonVisible("RetainerSellList")) return;
                if (currentSlot >= MaxMarketSlots)
                {
                    FinishAutomation();
                    return;
                }

                if (!FireRetainerSellListOpenItem(currentSlot))
                {
                    FinishAutomation();
                    return;
                }
                Transition(AutomationState.WaitContextMenu, $"Objet {currentSlot + 1}: ouverture...");
                return;

            case AutomationState.WaitContextMenu:
                if (IsAddonVisible("ContextMenu"))
                {
                    Transition(AutomationState.SelectAdjustPrice, "Menu contextuel ouvert.", immediate: true);
                    return;
                }
                if (IsAddonVisible("RetainerSell"))
                {
                    Transition(AutomationState.WaitRetainerSell, "Fenêtre de prix ouverte.", immediate: true);
                    return;
                }
                if (TimedOut())
                {
                    FinishAutomation();
                }
                return;

            case AutomationState.SelectAdjustPrice:
                if (!FireContextMenuFirstEntry())
                {
                    if (TimedOut()) Fail("Impossible de sélectionner Ajuster le prix.");
                    return;
                }
                Transition(AutomationState.WaitRetainerSell, "Ajuster le prix sélectionné.");
                return;

            case AutomationState.WaitRetainerSell:
                if (IsAddonVisible("RetainerSell"))
                {
                    var addon = GetRetainerSell();
                    if (addon == null) return;
                    currentOldPrice = addon->AskingPrice != null ? addon->AskingPrice->Value : 0;
                    var raw = addon->ItemName != null ? addon->ItemName->NodeText.ToString() : string.Empty;
                    selectedItemIsHq = raw.Contains('\uE03C');
                    pendingSuggestedPrice = null;
                    Transition(AutomationState.OpenCompare, $"Prix actuel {currentOldPrice:N0} gil.");
                    return;
                }
                if (TimedOut()) Fail("La fenêtre de prix ne s'est pas ouverte.");
                return;

            case AutomationState.OpenCompare:
                if (!ClickRetainerSellButton(ButtonKind.Compare))
                {
                    if (TimedOut()) Fail("Impossible de cliquer sur Comparer les prix.");
                    return;
                }
                awaitingMarketResult = true;
                Transition(AutomationState.WaitMarketWindow, "Comparaison des prix...");
                return;

            case AutomationState.WaitMarketWindow:
                if (IsAddonVisible("ItemSearchResult"))
                {
                    awaitingMarketResult = true;
                    Transition(AutomationState.WaitMarketOffers, "Market Board ouvert; attente des offres.", immediate: true);
                    return;
                }
                if (TimedOut()) Fail("Le Market Board ne s'est pas ouvert.");
                return;

            case AutomationState.WaitMarketOffers:
                if (pendingSuggestedPrice.HasValue)
                {
                    Transition(AutomationState.CloseMarket, "Prix calculé.", immediate: true);
                    return;
                }
                if (TimedOut())
                {
                    skippedCount++;
                    Transition(AutomationState.CloseMarket, "Aucune donnée reçue à temps; objet ignoré.", immediate: true);
                }
                return;

            case AutomationState.CloseMarket:
                CloseAddon("ItemSearchResult");
                Transition(AutomationState.WaitMarketClosed, "Fermeture du Market Board...");
                return;

            case AutomationState.WaitMarketClosed:
                if (!IsAddonVisible("ItemSearchResult"))
                {
                    if (pendingSuggestedPrice.HasValue)
                        Transition(AutomationState.ApplyPrice, "Application du nouveau prix.");
                    else
                    {
                        skippedCount++;
                        Transition(AutomationState.NextSlot, "Prix inchangé.");
                    }
                    return;
                }
                if (TimedOut()) Fail("Le Market Board ne s'est pas fermé.");
                return;

            case AutomationState.ApplyPrice:
                if (!pendingSuggestedPrice.HasValue)
                {
                    Transition(AutomationState.NextSlot, "Aucun prix à appliquer.");
                    return;
                }

                var target = (int)Math.Clamp((long)pendingSuggestedPrice.Value, 1L, int.MaxValue);
                if (target == currentOldPrice)
                {
                    skippedCount++;
                    if (!FireRetainerSellCancel())
                    {
                        if (TimedOut()) Fail("Impossible de fermer la fenêtre de prix.");
                        return;
                    }
                    Transition(AutomationState.WaitSellClosed, "Déjà au prix cible; aucun changement.");
                    return;
                }

                if (!SetRetainerSellPrice(target))
                {
                    if (TimedOut()) Fail("Impossible d'écrire le nouveau prix.");
                    return;
                }
                Transition(AutomationState.ConfirmPrice, $"Prix {currentOldPrice:N0} → {target:N0} gil.");
                return;

            case AutomationState.ConfirmPrice:
                if (!FireRetainerSellConfirm())
                {
                    if (TimedOut()) Fail("Impossible de confirmer le prix.");
                    return;
                }
                changedCount++;
                processedCount++;
                Transition(AutomationState.WaitSellClosed, "Prix confirmé.");
                return;

            case AutomationState.WaitSellClosed:
                if (!IsAddonVisible("RetainerSell"))
                {
                    Transition(AutomationState.NextSlot, "Retour à la liste.");
                    return;
                }
                if (TimedOut()) Fail("La fenêtre de prix ne s'est pas fermée.");
                return;

            case AutomationState.NextSlot:
                currentSlot++;
                pendingSuggestedPrice = null;
                awaitingMarketResult = false;
                Transition(AutomationState.OpenSlot, $"Passage à l'objet {currentSlot + 1}.");
                return;
        }
    }

    private void FinishAutomation()
    {
        automationState = AutomationState.Finished;
        awaitingMarketResult = false;
        status = $"Terminé : {changedCount} prix modifié(s), {skippedCount} ignoré(s).";
        Log.Information("Undercut All finished. changed={Changed}, skipped={Skipped}", changedCount, skippedCount);
    }

    private void Fail(string message)
    {
        automationState = AutomationState.Error;
        awaitingMarketResult = false;
        status = "Erreur : " + message;
        Log.Warning("Undercut automation stopped: {Message}", message);
    }

    private unsafe bool FireRetainerSellListOpenItem(int slotIndex0)
    {
        var ptr = GameGui.GetAddonByName("RetainerSellList");
        if (ptr.IsNull) return false;
        var unit = (AtkUnitBase*)ptr.Address;
        if (unit == null || !unit->IsVisible || !unit->IsReady) return false;

        var values = stackalloc AtkValue[3];
        values[0] = IntValue(0);
        values[1] = IntValue(slotIndex0);
        values[2] = IntValue(1);
        unit->FireCallback(3, values, true);
        return true;
    }

    private unsafe bool FireContextMenuFirstEntry()
    {
        var ptr = GameGui.GetAddonByName("ContextMenu");
        if (ptr.IsNull) return false;
        var unit = (AtkUnitBase*)ptr.Address;
        if (unit == null || !unit->IsVisible || !unit->IsReady) return false;

        var values = stackalloc AtkValue[2];
        values[0] = IntValue(0);
        values[1] = IntValue(0);
        unit->FireCallback(2, values, true);
        return true;
    }

    private enum ButtonKind { Compare }

    private unsafe bool ClickRetainerSellButton(ButtonKind kind)
    {
        var addon = GetRetainerSell();
        if (addon == null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return false;

        AtkComponentButton* button = kind switch
        {
            ButtonKind.Compare => addon->AtkUnitBase.GetComponentButtonById(4),
            _ => null,
        };

        Log.Debug("RetainerSell click requested: {Kind}", kind);
        return ClickButton(button, &addon->AtkUnitBase, kind);
    }

    private static unsafe bool ClickButton(AtkComponentButton* button, AtkUnitBase* addon, ButtonKind kind)
    {
        if (button == null || addon == null || !button->IsEnabled || button->AtkResNode == null || !button->AtkResNode->IsVisible())
            return false;

        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null) return false;
        var resNode = &ownerNode->AtkResNode;
        var evt = (AtkEvent*)resNode->AtkEventManager.Event;
        if (evt == null) return false;

        Log.Debug("RetainerSell {Kind} event: type={EventType}, param={Param}",
            kind, evt->State.EventType, evt->Param);

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, resNode->AtkEventManager.Event);
        return true;
    }

    private unsafe bool FireRetainerSellConfirm()
    {
        var addon = GetRetainerSell();
        if (addon == null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return false;

        Log.Debug("RetainerSell confirm via FireCallbackInt(0).");
        addon->AtkUnitBase.FireCallbackInt(0);
        return true;
    }

    private unsafe bool FireRetainerSellCancel()
    {
        var addon = GetRetainerSell();
        if (addon == null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return false;

        Log.Debug("RetainerSell cancel via FireCallbackInt(-1).");
        addon->AtkUnitBase.FireCallbackInt(-1);
        return true;
    }

    private unsafe bool SetRetainerSellPrice(int price)
    {
        price = Math.Max(1, price);
        var addon = GetRetainerSell();
        if (addon == null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return false;

        var values = stackalloc AtkValue[2];
        values[0] = IntValue(2);
        values[1] = IntValue(price);
        addon->AtkUnitBase.FireCallback(2, values, true);
        return true;
    }

    private static AtkValue IntValue(int value) => new() { Type = AtkValueType.Int, Int = value };

    private unsafe AddonRetainerSell* GetRetainerSell()
    {
        var ptr = GameGui.GetAddonByName("RetainerSell");
        return ptr.IsNull ? null : (AddonRetainerSell*)ptr.Address;
    }

    private unsafe bool IsAddonVisible(string name)
    {
        var ptr = GameGui.GetAddonByName(name);
        if (ptr.IsNull) return false;
        var unit = (AtkUnitBase*)ptr.Address;
        return unit != null && unit->IsVisible && unit->IsReady;
    }

    private unsafe void CloseAddon(string name)
    {
        var ptr = GameGui.GetAddonByName(name);
        if (ptr.IsNull) return;
        var unit = (AtkUnitBase*)ptr.Address;
        if (unit == null || !unit->IsVisible) return;
        unit->FireCallbackInt(-1);
    }
}
