using System;
using UnityEngine;

[Serializable]
public sealed class NetworkSimulationSettings
{
    public bool enabled = false;

    [Range(0, 500)]
    public int outgoingDelayMs = 50;

    [Range(0, 500)]
    public int incomingDelayMs = 50;

    [Range(0, 200)]
    public int jitterMs = 20;

    [Range(16, 4096)]
    public int maxQueueSize = 512;
}