using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Enums;
using Creche.Domain.Rules;

namespace Creche.Tests;

public class GroupCapacityTests
{
    [Theory]
    [InlineData(10, 9, false, CapacityCheck.Ok)]
    [InlineData(10, 10, false, CapacityCheck.Warn)]
    [InlineData(10, 10, true, CapacityCheck.Block)]
    [InlineData(10, 12, true, CapacityCheck.Block)]
    public void Capacity_rule(int capacity, int occupied, bool block, CapacityCheck expected)
        => Assert.Equal(expected, GroupRules.CheckCapacity(capacity, occupied, block));

    [Fact]
    public void Age_range_rule()
    {
        var today = new DateOnly(2026, 11, 16);
        Assert.Equal(22, GroupRules.AgeInMonths(new DateOnly(2025, 1, 10), today));
        Assert.True(GroupRules.AgeFits(new DateOnly(2025, 1, 10), today, 18, 36));
        Assert.False(GroupRules.AgeFits(new DateOnly(2026, 6, 1), today, 18, 36));
    }

    [Fact]
    public async Task Full_group_is_refused_when_the_creche_blocks()
    {
        using var host = await TestHost.CreateAsync();
        await host.UpdateSettingsAsync(s => s.BlockWhenGroupFull = true);
        var group = await host.AddGroupAsync(capacity: 2);
        await host.EnrolAsync("A", groupId: group.Id);
        await host.EnrolAsync("B", groupId: group.Id);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => host.EnrolAsync("C", groupId: group.Id, allowOverCapacity: true));

        Assert.Equal("GroupFull", error.Code);
        Assert.Equal(2, (await host.Get<IGroupService>().GetChildrenAsync(group.Id)).Count);
    }

    [Fact]
    public async Task Full_group_only_warns_by_default_and_accepts_after_confirmation()
    {
        using var host = await TestHost.CreateAsync();
        var group = await host.AddGroupAsync(capacity: 1);
        await host.EnrolAsync("A", groupId: group.Id);

        await Assert.ThrowsAsync<GroupFullWarningException>(() => host.EnrolAsync("B", groupId: group.Id));
        Assert.Single(await host.Get<IChildService>().ListAsync(new ChildFilter()));  // nothing was half-saved

        await host.EnrolAsync("B", groupId: group.Id, allowOverCapacity: true);
        var info = (await host.Get<IGroupService>().ListAsync()).Single();
        Assert.Equal(2, info.Occupied);
        Assert.Equal(0, info.Free);
        Assert.True(info.IsFull);
    }

    [Fact]
    public async Task Waiting_list_does_not_take_a_place_until_admission()
    {
        using var host = await TestHost.CreateAsync();
        await host.UpdateSettingsAsync(s => s.BlockWhenGroupFull = true);
        var group = await host.AddGroupAsync(capacity: 1);
        var waiting = await host.EnrolAsync("Waiting", groupId: group.Id, waitingList: true);
        var first = await host.EnrolAsync("First", groupId: group.Id);
        var enrolment = host.Get<IEnrolmentService>();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => enrolment.AdmitFromWaitingListAsync(waiting.Id, group.Id, null, new DateOnly(2026, 11, 16), 0m));
        Assert.Equal("GroupFull", error.Code);

        await enrolment.LeaveAsync(first.Id, new DateOnly(2026, 11, 15), "Moved");   // a place is freed
        await enrolment.AdmitFromWaitingListAsync(waiting.Id, group.Id, null, new DateOnly(2026, 11, 16), 0m);

        var child = (await host.Get<IGroupService>().GetChildrenAsync(group.Id)).Single();
        Assert.Equal(waiting.Id, child.Id);
        Assert.Equal(ChildStatus.Active, child.Status);
    }

    [Fact]
    public async Task Moving_a_child_to_a_full_group_follows_the_same_policy()
    {
        using var host = await TestHost.CreateAsync();
        await host.UpdateSettingsAsync(s => s.BlockWhenGroupFull = true);
        var small = await host.AddGroupAsync("Small", capacity: 1);
        var big = await host.AddGroupAsync("Big", capacity: 5);
        await host.EnrolAsync("A", groupId: small.Id);
        var mover = await host.EnrolAsync("B", groupId: big.Id);
        var children = host.Get<IChildService>();

        var file = (await children.GetAsync(mover.Id))!;
        file.GroupId = small.Id;
        await Assert.ThrowsAsync<BusinessRuleException>(() => children.SaveAsync(file));

        file.GroupId = big.Id;   // staying in the same group is never a capacity problem
        await children.SaveAsync(file);
    }

    [Fact]
    public async Task A_group_with_children_cannot_be_archived()
    {
        using var host = await TestHost.CreateAsync();
        var group = await host.AddGroupAsync();
        await host.EnrolAsync("A", groupId: group.Id);
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => host.Get<IGroupService>().ArchiveAsync(group.Id));
        Assert.Equal("GroupNotEmpty", error.Code);
    }
}
