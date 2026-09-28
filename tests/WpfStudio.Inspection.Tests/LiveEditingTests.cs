using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Inspection.Tests;

public sealed class LiveEditingTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task ScalarOverridesResetLocalStyleDefaultAndInheritedValuesAndDetachRestoresOtherDispatcher(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var target = await RuntimeEditingApplication.StartAsync(framework, timeout.Token);
        var original = await target.StateAsync(timeout.Token);
        foreach (var (name, property, source) in new[]
        {
            ("EditLiteral", "Value", "Local"), ("EditStyled", "Value", "Style"),
            ("EditDefault", "Value", "Default"), ("EditInherited", "InheritedValue", "Inherited")
        })
        {
            var before = await target.InspectAsync(name, timeout.Token);
            var beforeProperty = RuntimeEditingApplication.Property(before, property);
            Assert.True(beforeProperty.CanEdit, beforeProperty.EditUnavailableReason);
            Assert.Equal(source, beforeProperty.ValueSource);
            var request = RuntimeEditingApplication.Request(before, property, "Temporary " + name);
            Assert.True((await target.Session.ValidatePropertyAsync(request, timeout.Token)).Success);
            Assert.Equal(beforeProperty.Value, RuntimeEditingApplication.Property(await target.InspectAsync(name, timeout.Token), property).Value);
            var applied = await target.Session.SetPropertyAsync(request, timeout.Token);
            Assert.Equal("Applied", applied.Outcome);
            Assert.True(RuntimeEditingApplication.Property(applied.Element!, property).IsOverridden);
            Assert.Equal("Temporary " + name, RuntimeEditingApplication.Property(applied.Element!, property).EditableValue);
            var reset = await target.EditAsync(name, property, null, timeout.Token, reset: true);
            Assert.Equal("Reset", reset.Outcome);
            var restored = RuntimeEditingApplication.Property(reset.Element!, property);
            Assert.False(restored.IsOverridden);
            Assert.Equal(beforeProperty.Value, restored.Value);
            Assert.Equal(source, restored.ValueSource);
        }
        var afterReset = await target.StateAsync(timeout.Token);
        foreach (var name in new[] { "EditLiteral", "EditStyled", "EditDefault" })
        {
            Assert.Equal(original[name].Value, afterReset[name].Value);
            Assert.Equal(original[name].HasLocalValue, afterReset[name].HasLocalValue);
        }
        Assert.Equal("Applied", (await target.EditAsync("EditLiteral", "Value", "Pending detach", timeout.Token)).Outcome);
        Assert.Equal("Applied", (await target.EditAsync("EditStyled", "Value", "Pending detach", timeout.Token)).Outcome);
        Assert.Equal("Applied", (await target.EditAsync("ChildPickButton", "Width", "222.5", timeout.Token)).Outcome);
        Assert.Equal(222.5, (await target.StateAsync(timeout.Token)).ChildWidth);
        await target.Session.DisconnectAsync(timeout.Token);
        var detached = await WaitForStateAsync(target, state => state["EditLiteral"].Value == original["EditLiteral"].Value &&
            state["EditStyled"].Value == original["EditStyled"].Value && state.ChildWidth == original.ChildWidth, timeout.Token);
        Assert.Equal(original["EditStyled"].HasLocalValue, detached["EditStyled"].HasLocalValue);
        Assert.Equal("ping", await target.App.SendAsync("ping", timeout.Token));
    }

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task BindingOverridesNeverWriteModelsAndResetRestoresTwoWayStyleMultiAndPriorityBindings(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var target = await RuntimeEditingApplication.StartAsync(framework, timeout.Token);
        foreach (var name in new[] { "EditTwoWay", "EditStyleTwoWay", "EditMulti", "EditPriority" })
        {
            var original = (await target.StateAsync(timeout.Token))[name];
            var initial = RuntimeEditingApplication.Property(await target.InspectAsync(name, timeout.Token), "Value");
            Assert.True(initial.CanEdit, initial.EditUnavailableReason);
            Assert.Equal("Applied", (await target.EditAsync(name, "Value", "Temporary target only", timeout.Token)).Outcome);
            var overridden = (await target.StateAsync(timeout.Token))[name];
            Assert.Equal("Temporary target only", overridden.Value);
            Assert.Equal(original.SourceValue, overridden.SourceValue);
            Assert.Equal(original.SourceWrites, overridden.SourceWrites);
            await target.App.SendAsync("edit-model-update", timeout.Token);
            var modelChanged = (await target.StateAsync(timeout.Token))[name];
            Assert.Equal("Temporary target only", modelChanged.Value);
            Assert.Equal("edit-model-update: " + name, modelChanged.SourceValue);
            Assert.Equal(original.SourceWrites, modelChanged.SourceWrites);
            var reset = await target.EditAsync(name, "Value", null, timeout.Token, reset: true);
            Assert.Equal("Reset", reset.Outcome);
            var restored = (await target.StateAsync(timeout.Token))[name];
            Assert.Equal(original.BindingKind, restored.BindingKind);
            Assert.Equal(original.BindingIdentity, restored.BindingIdentity);
            Assert.Equal(original.SourceWrites, restored.SourceWrites);
            Assert.Equal("edit-model-update: " + name + (name == "EditMulti" ? " | tail" : ""), restored.Value);
            await target.App.SendAsync("edit-model-update-again", timeout.Token);
            var resumed = (await target.StateAsync(timeout.Token))[name];
            Assert.Equal("edit-model-update-again: " + name + (name == "EditMulti" ? " | tail" : ""), resumed.Value);
            Assert.Equal(original.SourceWrites, resumed.SourceWrites);
        }
        foreach (var name in new[] { "EditOneWayToSource", "EditStyleOneWayToSource" })
        {
            var before = (await target.StateAsync(timeout.Token))[name];
            var element = await target.InspectAsync(name, timeout.Token);
            var property = RuntimeEditingApplication.Property(element, "Value");
            Assert.False(property.CanEdit);
            Assert.Contains("OneWayToSource", property.EditUnavailableReason);
            var request = new InspectionPropertyEdit(Guid.NewGuid().ToString("N"), element.Revision, element.NodeId,
                property.PropertyId!, property.EditToken ?? "", "Must not reach model");
            Assert.False((await target.Session.ValidatePropertyAsync(request, timeout.Token)).Success);
            Assert.Equal("Rejected", (await target.Session.SetPropertyAsync(request, timeout.Token)).Outcome);
            var after = (await target.StateAsync(timeout.Token))[name];
            Assert.Equal(before.Value, after.Value);
            Assert.Equal(before.SourceValue, after.SourceValue);
            Assert.Equal(before.SourceWrites, after.SourceWrites);
            Assert.Equal(before.BindingIdentity, after.BindingIdentity);
        }
    }

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task NullEmptyLongAndInvariantValuesUseExactPropertyIdentityAndValidationDoesNotMutate(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var target = await RuntimeEditingApplication.StartAsync(framework, timeout.Token);
        var nullable = await target.InspectAsync("EditNullable", timeout.Token);
        Assert.True(RuntimeEditingApplication.Property(nullable, "NullableValue").IsNull);
        Assert.Equal(5000, RuntimeEditingApplication.Property(nullable, "LongValue").EditableValue!.Length);
        Assert.Equal("Applied", (await target.EditAsync("EditNullable", "NullableValue", "", timeout.Token)).Outcome);
        Assert.Equal("", (await target.StateAsync(timeout.Token))["EditNullable"].NullableValue);
        Assert.False(RuntimeEditingApplication.Property(await target.InspectAsync("EditNullable", timeout.Token), "NullableValue").IsNull);
        Assert.Equal("Applied", (await target.EditAsync("EditNullable", "NullableValue", null, timeout.Token, isNull: true)).Outcome);
        Assert.Null((await target.StateAsync(timeout.Token))["EditNullable"].NullableValue);
        Assert.Equal("Reset", (await target.EditAsync("EditNullable", "NullableValue", null, timeout.Token, reset: true)).Outcome);
        var longValue = new string('N', 6000) + "\r\n{}<&\"'";
        Assert.Equal("Applied", (await target.EditAsync("EditNullable", "LongValue", longValue, timeout.Token)).Outcome);
        Assert.Equal(longValue, (await target.StateAsync(timeout.Token))["EditNullable"].LongValue);
        Assert.Equal(longValue, RuntimeEditingApplication.Property(await target.InspectAsync("EditNullable", timeout.Token), "LongValue").EditableValue);
        Assert.Equal("Reset", (await target.EditAsync("EditNullable", "LongValue", null, timeout.Token, reset: true)).Outcome);
        Assert.Equal(5000, (await target.StateAsync(timeout.Token))["EditNullable"].LongValue.Length);
        await target.App.SendAsync("edit-culture-french", timeout.Token);
        foreach (var value in new[] { "not a number", "-1", "42,75" })
        {
            var request = await target.RequestAsync("EditNullable", "Number", value, timeout.Token);
            Assert.False((await target.Session.ValidatePropertyAsync(request, timeout.Token)).Success);
            Assert.Equal("Rejected", (await target.Session.SetPropertyAsync(request, timeout.Token)).Outcome);
            Assert.Equal(12.5, (await target.StateAsync(timeout.Token))["EditNullable"].Number);
        }
        var nullNumber = await target.RequestAsync("EditNullable", "Number", null, timeout.Token, isNull: true);
        Assert.False((await target.Session.ValidatePropertyAsync(nullNumber, timeout.Token)).Success);
        Assert.Equal("Applied", (await target.EditAsync("EditNullable", "Number", "42.75", timeout.Token)).Outcome);
        Assert.Equal(42.75, (await target.StateAsync(timeout.Token))["EditNullable"].Number);
        Assert.Equal("Applied", (await target.EditAsync("EditNullable", "PaddingValue", "1.5,2.5,3.5,4.5", timeout.Token)).Outcome);
        Assert.Equal("1.5,2.5,3.5,4.5", (await target.StateAsync(timeout.Token))["EditNullable"].PaddingValue);

        var collision = await target.InspectAsync("EditCollision", timeout.Token);
        var sameNamed = collision.Properties.Where(property => property.Name == "EditOptions.Mode").ToArray();
        Assert.Equal(2, sameNamed.Length);
        Assert.NotEqual(sameNamed[0].PropertyId, sameNamed[1].PropertyId);
        Assert.Equal("Applied", (await target.EditAsync("EditCollision", "EditOptions.Mode", "23", timeout.Token, ownerSuffix: "First.EditOptions")).Outcome);
        var firstChanged = (await target.StateAsync(timeout.Token))["EditCollision"];
        Assert.Equal(23, firstChanged.FirstMode);
        Assert.Equal("second original", firstChanged.SecondMode);
        Assert.Equal("Applied", (await target.EditAsync("EditCollision", "EditOptions.Mode", "Second temporary", timeout.Token, ownerSuffix: "Second.EditOptions")).Outcome);
        Assert.Equal(23, (await target.StateAsync(timeout.Token))["EditCollision"].FirstMode);
        Assert.Equal("Reset", (await target.EditAsync("EditCollision", "EditOptions.Mode", null, timeout.Token, reset: true, ownerSuffix: "First.EditOptions")).Outcome);
        Assert.Equal("Reset", (await target.EditAsync("EditCollision", "EditOptions.Mode", null, timeout.Token, reset: true, ownerSuffix: "Second.EditOptions")).Outcome);
        var restored = (await target.StateAsync(timeout.Token))["EditCollision"];
        Assert.Equal(7, restored.FirstMode);
        Assert.Equal("second original", restored.SecondMode);
    }

    [Fact]
    public async Task StaleTokensTreesAndRemovedTargetsRejectAndRepeatedOperationDoesNotReapply()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await using var target = await RuntimeEditingApplication.StartAsync("net10.0-windows", timeout.Token);
        var staleValue = await target.RequestAsync("EditLiteral", "Value", "Must not apply", timeout.Token);
        await target.App.SendAsync("edit-app-change-literal", timeout.Token);
        Assert.Equal("Conflict", (await target.Session.SetPropertyAsync(staleValue, timeout.Token)).Outcome);
        Assert.Equal("Application changed literal", (await target.StateAsync(timeout.Token))["EditLiteral"].Value);
        var staleTree = await target.RequestAsync("EditLiteral", "Value", "Must not apply", timeout.Token);
        await target.RefreshAsync(timeout.Token);
        var unchanged = await target.InspectAsync("EditLiteral", timeout.Token);
        Assert.NotEqual(staleTree.Revision, unchanged.Revision);
        Assert.Equal(staleTree.EditToken, RuntimeEditingApplication.Property(unchanged, "Value").EditToken);
        Assert.Equal("Conflict", (await target.Session.SetPropertyAsync(staleTree, timeout.Token)).Outcome);
        var request = await target.RequestAsync("EditLiteral", "Value", "Only once", timeout.Token);
        var first = await target.Session.SetPropertyAsync(request, timeout.Token);
        Assert.Equal("Applied", first.Outcome);
        var marker = (await target.StateAsync(timeout.Token))["EditLiteral"].BindingIdentity;
        var repeated = await target.Session.SetPropertyAsync(request, timeout.Token);
        Assert.Equal("Applied", repeated.Outcome);
        Assert.Equal(marker, (await target.StateAsync(timeout.Token))["EditLiteral"].BindingIdentity);
        Assert.Equal("Rejected", (await target.Session.SetPropertyAsync(request with { Value = "Different payload" }, timeout.Token)).Outcome);
        Assert.Equal("Only once", (await target.StateAsync(timeout.Token))["EditLiteral"].Value);
        Assert.Equal("Applied", (await target.Session.GetEditStatusAsync(new(request.OperationId), timeout.Token)).Outcome);
        Assert.Equal("Conflict", (await target.Session.SetPropertyAsync(request with { OperationId = Guid.NewGuid().ToString("N") }, timeout.Token)).Outcome);
        var removed = await target.RequestAsync("EditLiteral", "Value", "Cannot edit detached", timeout.Token);
        await target.App.SendAsync("edit-remove-target", timeout.Token);
        Assert.Equal("Conflict", (await target.Session.SetPropertyAsync(removed, timeout.Token)).Outcome);
    }

    [Fact]
    public async Task ResetAndDisconnectNeverOverwriteABindingTheApplicationInstalledAfterTheOverride()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var target = await RuntimeEditingApplication.StartAsync("net10.0-windows", timeout.Token);
        var applied = await target.EditAsync("EditTwoWay", "Value", "Temporary", timeout.Token);
        Assert.Equal("Applied", applied.Outcome);
        var oldReset = RuntimeEditingApplication.Request(applied.Element!, "Value", null, reset: true);
        await target.App.SendAsync("edit-app-replace-binding", timeout.Token);
        var before = (await target.StateAsync(timeout.Token))["EditTwoWay"];
        Assert.Equal("Application replaced binding", before.Value);
        Assert.Equal("Conflict", (await target.Session.SetPropertyAsync(oldReset, timeout.Token)).Outcome);
        Assert.Equal(before.BindingIdentity, (await target.StateAsync(timeout.Token))["EditTwoWay"].BindingIdentity);
        await target.Session.DisconnectAsync(timeout.Token);
        var after = (await target.StateAsync(timeout.Token))["EditTwoWay"];
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.BindingIdentity, after.BindingIdentity);
        Assert.Equal(before.SourceWrites, after.SourceWrites);
    }

    private static async Task<EditingState> WaitForStateAsync(RuntimeEditingApplication target,
        Func<EditingState, bool> predicate, CancellationToken token)
    {
        while (true)
        {
            var state = await target.StateAsync(token);
            if (predicate(state)) return state;
            await Task.Delay(20, token);
        }
    }
}
