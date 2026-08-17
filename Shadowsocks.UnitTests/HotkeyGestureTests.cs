using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Hotkeys;

namespace Shadowsocks.UnitTests;

[TestClass]
public class HotkeyGestureTests
{
    [TestMethod]
    [DataRow("Ctrl+A", "Ctrl+A")]
    [DataRow("Ctrl+Alt+D2", "Ctrl+Alt+D2")]
    [DataRow("Ctrl+Alt+Shift+NumPad7", "Ctrl+Alt+Shift+NumPad7")]
    [DataRow("Ctrl+Shift+Alt+F6", "Ctrl+Alt+Shift+F6")]
    public void HotkeyGestureRoundTrips(string input, string canonical)
    {
        Assert.IsTrue(HotkeyGesture.TryParse(input, out HotkeyGesture gesture));
        Assert.AreEqual(canonical, gesture.ToString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("A")]
    [DataRow("Ctrl+")]
    [DataRow("Ctrl+DefinitelyNotAKey")]
    public void InvalidHotkeyGestureIsRejected(string input)
    {
        Assert.IsFalse(HotkeyGesture.TryParse(input, out _));
    }
}
