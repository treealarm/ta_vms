using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using web_srv.Authorization;

namespace web_srv.Tests;

// The camera's arming surface: what an operator is allowed to command, and what an administrator is
// allowed to set. Inheriting `control` down the tree is VmsMap's answer and is tested there.
public class CameraStateRightsTests
{
  [Fact]
  public void ReadingACameraStateIsViewLive()
  {
    var required = typeof(CameraStateController)
      .GetMethod(nameof(CameraStateController.GetState))!
      .GetCustomAttribute<RequiresRightAttribute>();

    Assert.NotNull(required);
    Assert.Equal(RightActions.ViewLive, required.Action);
  }

  [Theory]
  [InlineData(nameof(CameraStateController.SetState))]
  [InlineData(nameof(CameraStateController.SetArmMode))]
  public void ArmingACameraIsControl(string action)
  {
    var required = typeof(CameraStateController).GetMethod(action)!
      .GetCustomAttribute<RequiresRightAttribute>();

    Assert.NotNull(required);
    Assert.Equal(RightActions.Control, required.Action);
  }

  [Fact]
  public void EveryCameraStateActionDeclaresWhatItNeeds()
  {
    var actions = typeof(CameraStateController)
      .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
      .Where(m => m.GetCustomAttribute<HttpMethodAttribute>(inherit: true) is not null)
      .ToList();

    Assert.NotEmpty(actions);
    foreach (var action in actions)
    {
      var declared = action.GetCustomAttribute<RequiresRightAttribute>() is not null
        || action.GetCustomAttribute<NoRightsAttribute>() is not null;
      Assert.True(declared, $"{action.Name} declares no right, so RightsFilter would refuse it");
    }
  }
}
