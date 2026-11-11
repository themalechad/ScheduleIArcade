using MelonLoader;
using Il2CppScheduleOne.Money;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Games;
using ScheduleIArcade.Games.AirHockey;
using ScheduleIArcade.Models;
using UnityEngine;

namespace ScheduleIArcade.Services;

internal sealed class ArcadeSessionService : IDisposable
{
    public const float PlayPrice = 10f;

    private readonly ArcadeGameRegistry _games;
    private readonly ArcadeTvViewService _tvView = new();
    private IArcadeGame? _activeGame;
    private ArcadeCabinet? _activeCabinet;
    private Material? _screenMaterial;
    private Texture? _originalBaseMap;
    private Texture? _originalMainTexture;
    private Texture? _originalEmissionMap;

    private readonly ArcadePreferences _preferences;

    public ArcadeSessionService(ArcadeGameRegistry games, ArcadePreferences preferences)
    {
        _games = games;
        _preferences = preferences;
    }

    public bool IsPlaying => _activeGame != null;
    public ArcadeCabinet? ActiveCabinet => _activeCabinet;
    public string LastStartFailure { get; private set; } = string.Empty;

    public bool Start(ArcadeCabinet cabinet)
    {
        LastStartFailure = string.Empty;
        if (IsPlaying || cabinet.IsOccupied)
        {
            LastStartFailure = "MACHINE IN USE";
            return false;
        }

        if (!_games.TryCreate(cabinet.GameId, out var game) || game == null)
        {
            LastStartFailure = "MACHINE IS NOT READY";
            MelonLogger.Error($"No arcade game runtime is registered for '{cabinet.GameId}'.");
            return false;
        }

        MoneyManager? money = MoneyManager.Instance;
        if (money == null)
        {
            LastStartFailure = "CASH SYSTEM NOT READY — TRY AGAIN";
            game.Dispose();
            return false;
        }

        float availableCash = money.cashBalance;
        if (availableCash + 0.001f < PlayPrice)
        {
            LastStartFailure = $"NOT ENOUGH CASH — ${availableCash:0.00} AVAILABLE";
            game.Dispose();
            return false;
        }

        bool paymentTaken = false;
        try
        {
            game.Initialize();
            game.Start();

            // Doom cabinets run the bundled engine + Freedoom. If those files are missing the cabinet
            // can only show its test screen, so never charge.
            if (game is EmbeddedDoomArcadeGame doom && doom.IsUsingFallback)
            {
                LastStartFailure = "DOOM OUT OF ORDER — NO CHARGE";
                MelonLogger.Warning($"{cabinet.Id}: {doom.DisplayName} refused to start without charging: {doom.FallbackReason}");
                game.Dispose();
                return false;
            }

            if (game is EmbeddedDoomArcadeGame doomCabinet)
                doomCabinet.AttachAudio(cabinet.ScreenRenderer.transform, _preferences);
            if (game is SpaceCadetPinballGame pinballCabinet)
                pinballCabinet.AttachAudio(cabinet.ScreenRenderer.transform, _preferences);
            if (game is AirHockeyGame airHockey)
                airHockey.AttachAudio(cabinet.ScreenRenderer.transform, _preferences);

            // Two-player tables charge only once both sides are occupied and the match starts
            // (see Tick); opening the table and waiting for player 2 is free.
            if (game is not IDeferredChargeGame)
            {
                money.ChangeCashBalance(-PlayPrice, visualizeChange: true, playCashSound: true);
                paymentTaken = true;
            }
            _screenMaterial = cabinet.ScreenRenderer.material;
            CaptureOriginalScreen(_screenMaterial);
            ApplyTexture(_screenMaterial, game.ScreenTexture);

            cabinet.IsOccupied = true;
            _activeCabinet = cabinet;
            _activeGame = game;
            if (cabinet.Id.Equals("TV", StringComparison.OrdinalIgnoreCase) ||
                cabinet.Id.Equals("PACMAN", StringComparison.OrdinalIgnoreCase) ||
                cabinet.GameId.Equals("doom1", StringComparison.OrdinalIgnoreCase) ||
                cabinet.GameId.Equals("doom2", StringComparison.OrdinalIgnoreCase) ||
                cabinet.GameId.Equals("space_cadet_pinball", StringComparison.OrdinalIgnoreCase) ||
                cabinet.GameId.Equals("air_hockey", StringComparison.OrdinalIgnoreCase))
                _tvView.Enter(cabinet);
            MelonLogger.Msg(paymentTaken
                ? $"Started {game.DisplayName} on cabinet {cabinet.Id} after a ${PlayPrice:0.00} cash payment."
                : $"Opened {game.DisplayName} on cabinet {cabinet.Id}; ${PlayPrice:0.00} is charged only when the match starts.");
            return true;
        }
        catch (Exception ex)
        {
            if (paymentTaken)
            {
                try
                {
                    money.ChangeCashBalance(PlayPrice, visualizeChange: true, playCashSound: false);
                    LastStartFailure = "MACHINE COULD NOT START — $10 REFUNDED";
                }
                catch (Exception refundEx)
                {
                    LastStartFailure = "MACHINE COULD NOT START — CHECK CASH BALANCE";
                    MelonLogger.Error($"Failed to refund arcade payment: {refundEx}");
                }
            }
            else
            {
                LastStartFailure = "MACHINE COULD NOT START — NO CHARGE";
            }

            MelonLogger.Error($"Failed to start cabinet {cabinet.Id}: {ex}");
            try { game.Stop(); }
            catch { }
            game.Dispose();
            return false;
        }
    }

    public void Tick(float deltaTime)
    {
        if (_activeGame == null)
            return;

        _tvView.Tick();
        _activeGame.Tick(deltaTime);
        if (_activeGame is IDeferredChargeGame deferred && deferred.ReadyToCharge && !deferred.Charged)
            ChargeDeferred(deferred);
        if (_activeGame is EmbeddedDoomArcadeGame quitDoom && quitDoom.QuitRequested)
        {
            MelonLogger.Msg($"{quitDoom.DisplayName}: player quit from the Doom menu.");
            Stop();
            return;
        }

        if (_activeGame is EmbeddedDoomArcadeGame failedDoom && failedDoom.IsUsingFallback)
        {
            // The external Doom runtime died mid-session. Refund and end instead of leaving a
            // paid session on a test pattern.
            MelonLogger.Warning($"{failedDoom.DisplayName} stopped during play ({failedDoom.FallbackReason}); refunding ${PlayPrice:0.00}.");
            try { MoneyManager.Instance?.ChangeCashBalance(PlayPrice, visualizeChange: true, playCashSound: false); }
            catch (Exception ex) { MelonLogger.Error($"Failed to refund Doom session: {ex.Message}"); }
            Stop();
            return;
        }
        if (_screenMaterial != null)
            ApplyTexture(_screenMaterial, _activeGame.ScreenTexture);

        if (Input.GetKeyDown(KeyCode.Escape))
            Stop();
    }

    private void ChargeDeferred(IDeferredChargeGame game)
    {
        MoneyManager? money = MoneyManager.Instance;
        if (money == null)
        {
            game.ChargeRejected("CASH SYSTEM NOT READY");
            return;
        }
        float cash = money.cashBalance;
        if (cash + 0.001f < PlayPrice)
        {
            game.ChargeRejected($"NOT ENOUGH CASH ${cash:0.00}");
            return;
        }
        money.ChangeCashBalance(-PlayPrice, visualizeChange: true, playCashSound: true);
        game.ChargeAccepted();
        MelonLogger.Msg($"{_activeGame?.DisplayName}: both sides joined; match started after a ${PlayPrice:0.00} cash payment.");
    }

    public void Stop()
    {
        if (_activeGame == null)
            return;

        try { _activeGame.Stop(); }
        finally
        {
            _tvView.Exit();
            RestoreOriginalScreen();
            _activeGame.Dispose();
            _activeGame = null;
            if (_activeCabinet != null)
                _activeCabinet.IsOccupied = false;
            _activeCabinet = null;
            _screenMaterial = null;
            _originalBaseMap = null;
            _originalMainTexture = null;
            _originalEmissionMap = null;
            MelonLogger.Msg("Arcade session ended.");
        }
    }

    private void CaptureOriginalScreen(Material material)
    {
        _originalBaseMap = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
        _originalMainTexture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
        _originalEmissionMap = material.HasProperty("_EmissionMap") ? material.GetTexture("_EmissionMap") : null;
    }

    private void RestoreOriginalScreen()
    {
        if (_screenMaterial == null)
            return;

        if (_screenMaterial.HasProperty("_BaseMap"))
            _screenMaterial.SetTexture("_BaseMap", _originalBaseMap);
        if (_screenMaterial.HasProperty("_MainTex"))
            _screenMaterial.SetTexture("_MainTex", _originalMainTexture);
        if (_screenMaterial.HasProperty("_EmissionMap"))
            _screenMaterial.SetTexture("_EmissionMap", _originalEmissionMap);
    }

    private static void ApplyTexture(Material material, Texture? texture)
    {
        if (texture == null)
            return;

        if (material.HasProperty("_BaseMap"))
            material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_EmissionMap"))
        {
            material.SetTexture("_EmissionMap", texture);
            material.EnableKeyword("_EMISSION");
        }
    }

    public void Dispose()
    {
        Stop();
        _tvView.Exit();
    }
}
