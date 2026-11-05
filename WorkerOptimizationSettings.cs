using System;
using System.Collections.Generic;

namespace RobloxWorker.Launcher.Models;

public enum RobloxWindowMode
{
    Minimized = 0,
    OffScreen = 1,
    MicroTiled = 2,
    Normal = 3
}

public enum RamCompressionLevel
{
    Extreme = 0,
    Balanced = 1,
    Light = 2,
    Disabled = 3
}

/// <summary>
/// Что делать, если пользователь сам закрыл одно из окон Roblox во время работы.
/// </summary>
public enum ManualWindowCloseBehavior
{
    /// <summary>
    /// Спросить пользователя: открыть окно снова (если закрыл случайно) или остановить всё задание с тайм-аутом.
    /// </summary>
    AskUser = 0,

    /// <summary>
    /// Всегда автоматически открывать закрытое окно заново без остановки задания.
    /// </summary>
    AlwaysReopenAutomatically = 1,

    /// <summary>
    /// Сразу останавливать все окна и включать прогрессивный тайм-аут.
    /// </summary>
    StopAllWithTimeoutImmediately = 2
}

public sealed class WorkerOptimizationSettings
{
    public RobloxWindowMode WindowMode { get; set; } = RobloxWindowMode.Minimized;
    public RamCompressionLevel RamMode { get; set; } = RamCompressionLevel.Extreme;

    /// <summary>
    /// Поведение при ручном закрытии одного окна Roblox.
    /// </summary>
    public ManualWindowCloseBehavior ManualCloseBehavior { get; set; } = ManualWindowCloseBehavior.AskUser;

    public int MaxRamMbPerWindow { get; set; } = 140;
    public int TargetFpsCap { get; set; } = 8;
    public int AntiAfkIntervalMinutes { get; set; } = 5;
    public bool EnableEcoQosAndIdlePriority { get; set; } = true;
    public bool SmartAntiAfkWakeup { get; set; } = true;
    public bool HideContractWarningDialog { get; set; } = false;

    /// <summary>
    /// Количество досрочных выходов за текущий день (увеличивает тайм-аут по кругу и сбрасывается в конце дня).
    /// </summary>
    public int DailyEarlyExitCount { get; set; } = 0;

    /// <summary>
    /// Дата последнего досрочного выхода в формате yyyy-MM-dd (для сброса счётчика в 00:00).
    /// </summary>
    public string LastEarlyExitDate { get; set; } = string.Empty;

    public bool AfkActionJump { get; set; } = true;
    public bool AfkActionWalkForwardBack { get; set; } = true;
    public bool AfkActionStrafeLeftRight { get; set; } = false;
    public bool AfkActionCameraRotate { get; set; } = true;
    public bool AfkActionCameraZoom { get; set; } = false;
    public bool AfkActionMouseClick { get; set; } = false;

    public string GetActiveActionsSummary()
    {
        var steps = new List<string>();
        if (AfkActionMouseClick) steps.Add("Клик ЛКМ");
        if (AfkActionJump) steps.Add("Прыжок (Space)");
        if (AfkActionWalkForwardBack) steps.Add("Ходьба (W→S)");
        if (AfkActionStrafeLeftRight) steps.Add("Стрейф (A→D)");
        if (AfkActionCameraRotate) steps.Add("Камера (←→)");
        if (AfkActionCameraZoom) steps.Add("Зум (I→O)");

        return steps.Count > 0 ? string.Join(" + ", steps) : "Прыжок (Space)";
    }

    public string GetWindowModeDisplayName() => WindowMode switch
    {
        RobloxWindowMode.Minimized => "Полное сворачивание",
        RobloxWindowMode.OffScreen => "Скрыто за экраном",
        RobloxWindowMode.MicroTiled => "Мини-плитка 200x150",
        RobloxWindowMode.Normal => "Обычное окно (без сворачивания)",
        _ => WindowMode.ToString()
    };
}
