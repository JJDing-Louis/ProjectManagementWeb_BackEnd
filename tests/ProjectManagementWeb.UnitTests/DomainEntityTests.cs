using Bogus;
using FluentAssertions;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using TaskStatus = ProjectManagementWeb.Domain.Enums.TaskStatus;

namespace ProjectManagementWeb.UnitTests;

public sealed class DomainEntityTests
{
    [TestCase(BusinessCodeType.Project, "PRJ-20260823000001")]
    [TestCase(BusinessCodeType.Task, "TASK-20260823000001")]
    public void 業務編號應使用Utc日期與六位流水號(BusinessCodeType codeType, string expected)
    {
        string code = BusinessCodeFormatter.Format(codeType, new DateOnly(2026, 8, 23), 1);

        code.Should().Be(expected);
    }

    [Test]
    public void BusinessCodeCounter應從一開始並逐次遞增()
    {
        var counter = new BusinessCodeCounter(BusinessCodeType.Project, new DateOnly(2026, 8, 23));

        counter.LastValue.Should().Be(1);
        counter.TryIncrement().Should().BeTrue();
        counter.LastValue.Should().Be(2);
    }

    [Test]
    public void RefreshToken撤銷後不可再使用且保留替代Token識別碼()
    {
        DateTimeOffset now = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
        var faker = new Faker { Random = new Randomizer(20260823) };
        var token = new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), faker.Random.Hash(64), now, now.AddDays(7));
        Guid replacementId = Guid.NewGuid();

        token.Revoke(now.AddMinutes(1), replacementId);

        token.IsActive(now.AddMinutes(2)).Should().BeFalse();
        token.RevokedAt.Should().Be(now.AddMinutes(1));
        token.ReplacedByTokenId.Should().Be(replacementId);
    }

    [TestCase(TaskStatus.Pending)]
    [TestCase(TaskStatus.InProgress)]
    [TestCase(TaskStatus.Blocked)]
    [TestCase(TaskStatus.Completed)]
    public void Task在Mvp階段允許更新為任一固定狀態(TaskStatus targetStatus)
    {
        DateTimeOffset now = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
        var task = new TaskItem(Guid.NewGuid(), "TASK-001", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "測試 Task", null, now, now.AddDays(1), now);

        task.UpdateStatus(targetStatus, now.AddMinutes(1));

        task.Status.Should().Be(targetStatus);
        task.UpdatedAt.Should().Be(now.AddMinutes(1));
    }
}
