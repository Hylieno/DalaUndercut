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
    private unsafe void DrawAttachedButton()
    {
        if (!IsRetainerContext())
            return;

        var addonPtr = GameGui.GetAddonByName("RetainerSellList");
        if (addonPtr.IsNull)
            addonPtr = GameGui.GetAddonByName("RetainerSell");
        if (addonPtr.IsNull)
            return;

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return;

        var x = addon->X + addon->GetScaledWidth(true) + 8f;
        var y = addon->Y + 28f;

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.SetNextWindowSize(new Vector2(190, 0), ImGuiCond.Always);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize;

        if (ImGui.Begin("###DalaLenoUndercutAttached", flags))
        {
            if (!IsAutomationRunning)
            {
                if (ImGui.Button("Undercut All", new Vector2(170, 32)))
                    StartAutomation();
            }
            else
            {
                if (ImGui.Button("STOP", new Vector2(170, 32)))
                    StopAutomation("Automation arrêtée manuellement.");
            }

            ImGui.TextDisabled($"v0.3 · délai {configuration.ActionDelayMs} ms");
            if (IsAutomationRunning)
                ImGui.TextWrapped($"Objet {Math.Min(currentSlot + 1, MaxMarketSlots)}/20 · {automationState}");
        }

        ImGui.End();
    }

    private void Draw()
    {
        DrawAttachedButton();

        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(560, 430), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("DalaLeno Undercut - v0.3.5", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped(status);
        ImGui.Separator();

        ImGui.Text($"Contexte servant : {(IsRetainerContext() ? "OUI" : "NON")}");
        ImGui.Text($"Automation : {automationState}");
        ImGui.Text($"Slot : {currentSlot + 1}/20");
        ImGui.Text($"Modifiés : {changedCount}   Ignorés : {skippedCount}");
        ImGui.Text($"Objet sélectionné : {selectedItemName}");
        ImGui.Text($"Item ID : {(selectedItemId == 0 ? "?" : selectedItemId.ToString())}");
        ImGui.Text($"Qualité détectée : {(selectedItemIsHq ? "HQ" : "NQ")}");
        ImGui.Text($"Attente Market Board : {(awaitingMarketResult ? "OUI" : "NON")}");

        ImGui.Separator();

        if (lastSnapshot is not null)
        {
            ImGui.Text($"Dernier résultat : {lastSnapshot.ItemName} {(lastSnapshot.IsHq ? "HQ" : "NQ")}");
            ImGui.Text($"Listings concurrents : {lastSnapshot.MatchingListings}");
            ImGui.Text($"Prix concurrent le plus bas : {FormatGil(lastSnapshot.CurrentLowestPrice)}");
            ImGui.Text($"Prix proposé : {FormatGil(lastSnapshot.SuggestedPrice)}");
        }
        else
        {
            ImGui.TextDisabled("Aucun résultat Market Board capturé pour le moment.");
        }

        ImGui.Separator();
        var amount = configuration.UndercutAmount;
        if (ImGui.InputInt("Undercut (gil)", ref amount))
        {
            configuration.UndercutAmount = Math.Max(0, amount);
            SaveConfiguration();
        }

        var minimum = configuration.MinimumPrice;
        if (ImGui.InputInt("Prix minimum", ref minimum))
        {
            configuration.MinimumPrice = Math.Max(1, minimum);
            SaveConfiguration();
        }

        var delay = configuration.ActionDelayMs;
        if (ImGui.SliderInt("Délai entre actions (ms)", ref delay, 300, 5000))
        {
            configuration.ActionDelayMs = delay;
            SaveConfiguration();
        }
        ImGui.TextDisabled("Défaut : 1000 ms. Le plugin attend aussi que chaque fenêtre soit réellement prête.");

        var ignoreOwn = configuration.IgnoreOwnRetainers;
        if (ImGui.Checkbox("Ignorer mes propres servants", ref ignoreOwn))
        {
            configuration.IgnoreOwnRetainers = ignoreOwn;
            SaveConfiguration();
        }

        var autoOpen = configuration.OpenDiagnosticWindowAutomatically;
        if (ImGui.Checkbox("Ouvrir cette fenêtre automatiquement", ref autoOpen))
        {
            configuration.OpenDiagnosticWindowAutomatically = autoOpen;
            SaveConfiguration();
        }

        if (IsAutomationRunning && ImGui.Button("Arrêter l'automation"))
            StopAutomation("Automation arrêtée manuellement.");

        ImGui.End();
    }

    private static string FormatGil(uint? price) => price.HasValue ? $"{price.Value:N0} gil" : "—";

    private void SanitizeConfiguration()
    {
        configuration.UndercutAmount = Math.Max(0, configuration.UndercutAmount);
        configuration.MinimumPrice = Math.Max(1, configuration.MinimumPrice);
        configuration.ActionDelayMs = Math.Clamp(configuration.ActionDelayMs, 300, 5000);
    }

    private void SaveConfiguration()
    {
        SanitizeConfiguration();
        PluginInterface.SavePluginConfig(configuration);
    }
}
