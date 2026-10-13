using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace WarpDrop;

public sealed record HotspotResult(bool Ok, string Message, bool FiveGhz);

