using System.Text;

namespace MeshCoreNet;

/// <summary>
/// Runtime device contract used by MeshDispatcher.
/// </summary>
public interface IMeshDevice
{
    string Name { get; }
    string Type { get; }
    Task StartAsync(CancellationToken cancellationToken, MeshDispatcher dispatcher);
    Task HandleFrameAsync(RadioFrame frame, CancellationToken cancellationToken);
    Task HandlePacketAsync(MeshPacket packet, CancellationToken cancellationToken);
}

public abstract class MeshDeviceBase : IMeshDevice
{
    protected MeshDeviceBase(string name, string type)
    {
        Name = name;
        Type = type;
    }

    public string Name { get; }
    public string Type { get; }

    public virtual Task StartAsync(CancellationToken cancellationToken, MeshDispatcher dispatcher)
    {
        Console.WriteLine($"Device '{Name}' ({Type}) is ready.");
        return Task.CompletedTask;
    }

    public virtual Task HandlePacketAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Device '{Name}' received {packet}.");
        return Task.CompletedTask;
    }

    public virtual Task HandleFrameAsync(RadioFrame frame, CancellationToken cancellationToken)
    {
        var packet = MeshPacket.Parse(frame.Packet);
        Console.WriteLine($"Device '{Name}' received frame {packet} rssi={frame.Rssi} snr={frame.Snr} internal={frame.IsInternal}.");
        return Task.CompletedTask;
    }
}

public sealed class CompanionDevice : MeshDeviceBase
{
    // Legacy placeholder retained for unknown/simple configs. CompanionRadioDevice is the real port.
    public CompanionDevice(string name) : base(name, "companion")
    {
    }

    public override Task StartAsync(CancellationToken cancellationToken, MeshDispatcher dispatcher)
    {
        Console.WriteLine($"Companion device '{Name}' is serving the mesh application bridge.");
        dispatcher.QueuePacket(MeshPacket.CreateAdvert($"Companion {Name}"));
        return Task.CompletedTask;
    }
}

public sealed class RoomDevice : MeshDeviceBase
{
    // Legacy placeholder retained for unknown/simple configs. RoomMeshDevice is the real room implementation.
    public RoomDevice(string name) : base(name, "room")
    {
    }

    public override Task StartAsync(CancellationToken cancellationToken, MeshDispatcher dispatcher)
    {
        Console.WriteLine($"Room device '{Name}' is accepting room traffic.");
        dispatcher.QueuePacket(MeshPacket.CreateAdvert($"Room {Name}"), priority: 4, timeoutSeconds: 15);
        return Task.CompletedTask;
    }
}

public sealed class RepeaterDevice : MeshDeviceBase
{
    // Legacy placeholder retained for unknown/simple configs. RepeaterMeshDevice is the real repeater implementation.
    public RepeaterDevice(string name) : base(name, "repeater")
    {
    }

    public override Task StartAsync(CancellationToken cancellationToken, MeshDispatcher dispatcher)
    {
        Console.WriteLine($"Repeater device '{Name}' is relaying packets.");
        dispatcher.QueuePacket(MeshPacket.CreateAdvert($"Repeater {Name}"), priority: 5, timeoutSeconds: 20);
        return Task.CompletedTask;
    }
}

public sealed class GenericMeshDevice : MeshDeviceBase
{
    public GenericMeshDevice(string name, string type) : base(name, type)
    {
    }
}
