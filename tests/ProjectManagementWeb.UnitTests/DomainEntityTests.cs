using Bogus;
using FluentAssertions;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using TaskStatus = ProjectManagementWeb.Domain.Enums.TaskStatus;

namespace ProjectManagementWeb.UnitTests;

public sealed class DomainEntityTests
{
    private static IEnumerable<TestCaseData> ProjectStatusTransitions =>
        from source in Enum.GetValues<ProjectStatus>()
        from target in Enum.GetValues<ProjectStatus>()
        select new TestCaseData(source, target).SetName($"Project狀態允許_{source}_轉為_{target}");

    private static IEnumerable<TestCaseData> TaskStatusTransitions =>
        from source in Enum.GetValues<TaskStatus>()
        from target in Enum.GetValues<TaskStatus>()
        select new TestCaseData(source, target).SetName($"Task狀態允許_{source}_轉為_{target}");

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

    // 測試案例：TC-ERR-AUTH-010、TC-ERR-AUTH-015、TC-ERR-AUTH-016（Token 生命週期單元邊界）
    // 測試結果：Passed（未啟用、2:59.999、3:00.000、已使用與已失效生命週期）
    // 上次測試時間：2026-09-15 22:44:47 +08:00
    [Test]
    public void Email驗證Token應遵守三分鐘邊界與啟用使用失效生命週期()
    {
        DateTimeOffset issuedAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var token = new EmailVerificationToken(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), issuedAt, issuedAt.AddMinutes(3));

        token.IsActive(issuedAt).Should().BeFalse("寄送完成前的 Token 不可使用");
        token.Activate(issuedAt);
        token.IsActive(issuedAt.AddMinutes(3).AddMilliseconds(-1)).Should().BeTrue();
        token.IsActive(issuedAt.AddMinutes(3)).Should().BeFalse("自簽發滿三分鐘起失效");

        var usedToken = new EmailVerificationToken(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('B', 64), issuedAt, issuedAt.AddMinutes(3));
        usedToken.Activate(issuedAt);
        usedToken.Use(issuedAt.AddMinutes(1));
        usedToken.IsActive(issuedAt.AddMinutes(1)).Should().BeFalse("已使用 Token 不可重放");

        var invalidatedToken = new EmailVerificationToken(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('C', 64), issuedAt, issuedAt.AddMinutes(3));
        invalidatedToken.Activate(issuedAt);
        invalidatedToken.Invalidate(issuedAt.AddMinutes(1));
        invalidatedToken.IsActive(issuedAt.AddMinutes(1)).Should().BeFalse("被最新版取代後不可再使用");
    }

    // 測試案例：TC-ST-TASK-013
    // 測試結果：Passed（16/16 狀態組合）
    // 上次測試時間：2026-09-16 10:05:02 +08:00
    [TestCaseSource(nameof(TaskStatusTransitions))]
    public void Task四種狀態應允許任意互轉(TaskStatus sourceStatus, TaskStatus targetStatus)
    {
        DateTimeOffset now = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
        var task = new TaskItem(Guid.NewGuid(), "TASK-001", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "測試 Task", null, now, now.AddDays(1), now);
        task.UpdateStatus(sourceStatus, now.AddMinutes(1));

        task.UpdateStatus(targetStatus, now.AddMinutes(2));

        task.Status.Should().Be(targetStatus);
        task.UpdatedAt.Should().Be(now.AddMinutes(2));
    }

    // 測試案例：TC-ST-PRJ-008
    // 測試結果：Passed（16/16 狀態組合）
    // 上次測試時間：2026-09-16 10:05:02 +08:00
    [TestCaseSource(nameof(ProjectStatusTransitions))]
    public void Project四種狀態應允許任意互轉(ProjectStatus sourceStatus, ProjectStatus targetStatus)
    {
        DateTimeOffset now = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        Guid ownerId = Guid.NewGuid();
        var project = new Project(Guid.NewGuid(), "PRJ-001", "專案", null, ownerId, "Asia/Taipei", now);
        project.Update("來源狀態", null, ownerId, "Asia/Taipei", sourceStatus, now.AddMinutes(1));
        int versionBeforeTargetUpdate = project.VersionNumber;

        project.Update("目標狀態", "說明", ownerId, "Asia/Taipei", targetStatus, now.AddMinutes(2));

        project.Status.Should().Be(targetStatus);
        project.VersionNumber.Should().Be(versionBeforeTargetUpdate + 1);
        project.UpdatedAt.Should().Be(now.AddMinutes(2));
    }
}
