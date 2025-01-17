﻿namespace SImulator.Implementation.ButtonManagers.Web;

/// <summary>
/// Defines a response to Web button press.
/// </summary>
/// <param name="UserName">Player server-defined name.</param>
/// <param name="Token">Token to identify the player on next presses.</param>
public readonly record struct PressResponse(string UserName, string Token);
