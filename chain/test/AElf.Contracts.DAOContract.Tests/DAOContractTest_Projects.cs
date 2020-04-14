using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Contracts.Consensus.AEDPoS;
using AElf.Contracts.MultiToken;
using AElf.Contracts.Profit;
using AElf.Sdk.CSharp;
using AElf.Types;
using Shouldly;
using Xunit;

namespace AElf.Contracts.DAOContract
{
    public partial class DaoContractTest
    {
        private const string InvestmentProjectPullRequestUrl = "https://github.com/AElfProject/AElf/pull/111111";
        private const string InvestmentProjectCommitId = "investmentprojectcommitid";
        private const string InvestmentProjectDeliverPullRequestUrl = "https://github.com/AElfProject/AElf/pull/222222";
        private const string InvestmentProjectDeliverCommitId = "investmentprojectdelivercommitid";

        private const long InvestAmount = 1000_00000000;

        private List<BudgetPlan> BudgetPlans => new List<BudgetPlan>
        {
            new BudgetPlan
            {
                Index = 0,
                Phase = 1,
                Symbol = "ELF",
                Amount = 1000_00000000,
                ReceiverAddress = AliceAddress
            }
        };

        [Fact]
        public async Task<Hash> ProposeProjectToDAO_Test()
        {
            await InitialDAOContract();

            // Alice want to propose a project to DAO.
            var proposalId = (await AliceDAOContractStub.ProposeProjectToDAO.SendAsync(new ProposeProjectInput
            {
                PullRequestUrl = InvestmentProjectPullRequestUrl,
                CommitId = InvestmentProjectCommitId
            })).Output;

            // Check proposal exists and correct.
            var proposalInfo = await AssociationContractStub.GetProposal.CallAsync(proposalId);
            proposalInfo.ContractMethodName.ShouldBe(nameof(DAOContractStub.AddInvestmentProject));
            proposalInfo.ToAddress.ShouldBe(DAOContractAddress);

            return proposalId;
        }

        [Fact]
        public async Task<Hash> ProposeProjectToDAO_Approve_Test()
        {
            var proposalId = await ProposeProjectToDAO_Test();
            var projectId = await DAOContractStub.CalculateProjectId.CallAsync(new ProposeProjectInput
            {
                PullRequestUrl = InvestmentProjectPullRequestUrl,
                CommitId = InvestmentProjectCommitId
            });

            await DAOApproveAsync(proposalId);
            // Anyone call this method to release this proposal.
            await DAOContractStub.ReleaseProposal.SendAsync(new ReleaseProposalInput
            {
                ProjectId = projectId,
                ProposalId = proposalId
            });

            // Check project info.
            var projectInfo = await DAOContractStub.GetProjectInfo.CallAsync(projectId);
            projectInfo.PullRequestUrl.ShouldBe(InvestmentProjectPullRequestUrl);
            projectInfo.CommitId.ShouldBe(InvestmentProjectCommitId);
            projectInfo.VirtualAddress.ShouldNotBeNull();
            projectInfo.Status.ShouldBe(ProjectStatus.Proposed);

            return projectId;
        }

        [Fact]
        public async Task<Hash> ProposeProjectToParliament_Test()
        {
            var projectId = await ProposeProjectToDAO_Approve_Test();

            // After approved by DAO, Alice propose this project to Parliament.
            var result = await AliceDAOContractStub.ProposeProjectToParliament.SendAsync(
                new ProposeProjectWithBudgetsInput
                {
                    ProjectId = projectId,
                    BudgetPlans = {BudgetPlans}
                });
            var proposalId = result.Output;

            await CheckProjectStatus(projectId, ProjectStatus.Proposed);

            await ParliamentApproveAsync(proposalId);

            // Anyone call this method to release this proposal.
            await DAOContractStub.ReleaseProposal.SendAsync(new ReleaseProposalInput
            {
                ProjectId = projectId,
                ProposalId = proposalId,
                IsParliamentProposal = true
            });

            // Check project info.
            var projectInfo = await DAOContractStub.GetProjectInfo.CallAsync(projectId);
            projectInfo.PullRequestUrl.ShouldBe(InvestmentProjectPullRequestUrl);
            projectInfo.CommitId.ShouldBe(InvestmentProjectCommitId);
            projectInfo.Status.ShouldBe(ProjectStatus.Approved);
            projectInfo.ProfitSchemeId.ShouldNotBeNull();
            projectInfo.BudgetPlans.ShouldBe(BudgetPlans);
            projectInfo.VirtualAddress.ShouldNotBeNull();

            return projectId;
        }

        [Fact]
        public async Task<Hash> InvestToInvestmentProjectTest()
        {
            var projectId = await ProposeProjectToParliament_Test();

            await CheckProjectStatus(projectId, ProjectStatus.Approved);

            await AliceTokenContractStub.Approve.SendAsync(new ApproveInput
            {
                Spender = DAOContractAddress,
                Symbol = "ELF",
                Amount = InvestAmount
            });
            await AliceDAOContractStub.Invest.SendAsync(new InvestInput
            {
                ProjectId = projectId,
                Symbol = "ELF",
                Amount = InvestAmount
            });

            var budgetPlan = await DAOContractStub.GetBudgetPlan.CallAsync(new GetBudgetPlanInput
            {
                ProjectId = projectId,
                BudgetPlanIndex = 0
            });
            budgetPlan.Amount.ShouldBe(InvestAmount);
            budgetPlan.PaidInAmount.ShouldBe(InvestAmount);
            budgetPlan.Phase.ShouldBe(1);
            
            await CheckProjectStatus(projectId, ProjectStatus.Ready);

            return projectId;
        }

        [Fact]
        public async Task DeliverProjectTest()
        {
            var projectId = await InvestToInvestmentProjectTest();

            // Alice want to deliver project.
            var proposalId = (await AliceDAOContractStub.ProposeDeliver.SendAsync(new ProposeAuditionInput
            {
                ProjectId = projectId,
                DeliverPullRequestUrl = InvestmentProjectDeliverPullRequestUrl,
                DeliverCommitId = InvestmentProjectDeliverCommitId,
                BudgetPlanIndex = 0
            })).Output;

            await CheckProjectStatus(projectId, ProjectStatus.Ready);

            await DAOApproveAsync(proposalId);
            await DAOContractStub.ReleaseProposal.SendAsync(new ReleaseProposalInput
            {
                ProjectId = projectId,
                ProposalId = proposalId,
            });

            await CheckProjectStatus(projectId, ProjectStatus.Delivered);

            var budgetPlan = await DAOContractStub.GetBudgetPlan.CallAsync(new GetBudgetPlanInput
            {
                ProjectId = projectId,
                BudgetPlanIndex = 0
            });
            budgetPlan.DeliverPullRequestUrl.ShouldNotBeEmpty();
            budgetPlan.DeliverCommitId.ShouldNotBeEmpty();

            {
                var balance = await TokenContractStub.GetBalance.CallAsync(new GetBalanceInput
                {
                    Owner = AliceAddress,
                    Symbol = "ELF"
                });
                balance.Balance.ShouldBe(10_0000_0000_00000000 - InvestAmount);
            }

            // Alice gonna take rewards.
            var projectInfo = await DAOContractStub.GetProjectInfo.CallAsync(projectId);
            var result = await AliceProfitContractStub.ClaimProfits.SendAsync(new ClaimProfitsInput
            {
                SchemeId = projectInfo.ProfitSchemeId,
                Beneficiary = AliceAddress
            });
            result.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);

            {
                var balance = await TokenContractStub.GetBalance.CallAsync(new GetBalanceInput
                {
                    Owner = AliceAddress,
                    Symbol = "ELF"
                });
                balance.Balance.ShouldBe(10_0000_0000_00000000);
            }

        }

        private async Task CheckProjectStatus(Hash projectId, ProjectStatus status)
        {
            status.ShouldBe((await DAOContractStub.GetProjectInfo.CallAsync(projectId)).Status);
        }
    }
}