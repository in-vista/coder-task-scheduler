using System;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Modules.Branches.Interfaces;
using WiserTaskScheduler.Modules.Branches.Models;
using WiserTaskScheduler.Modules.CleanupItems.Interfaces;
using WiserTaskScheduler.Modules.CleanupItems.Models;
using WiserTaskScheduler.Modules.CleanupWiserHistory.Interfaces;
using WiserTaskScheduler.Modules.CleanupWiserHistory.Models;
using WiserTaskScheduler.Modules.Communications.Interfaces;
using WiserTaskScheduler.Modules.Communications.Models;
using WiserTaskScheduler.Modules.DocumentStoreRead.Interfaces;
using WiserTaskScheduler.Modules.DocumentStoreRead.Models;
using WiserTaskScheduler.Modules.GenerateFiles.Interfaces;
using WiserTaskScheduler.Modules.GenerateFiles.Models;
using WiserTaskScheduler.Modules.HttpApis.Interfaces;
using WiserTaskScheduler.Modules.HttpApis.Models;
using WiserTaskScheduler.Modules.ImportFiles.Interfaces;
using WiserTaskScheduler.Modules.ImportFiles.Models;
using WiserTaskScheduler.Modules.Queries.Interfaces;
using WiserTaskScheduler.Modules.Queries.Models;
using WiserTaskScheduler.Modules.ServerMonitors.Interfaces;
using WiserTaskScheduler.Modules.ServerMonitors.Models;
using WiserTaskScheduler.Modules.WiserImports.Interfaces;
using WiserTaskScheduler.Modules.WiserImports.Models;
using WiserTaskScheduler.Modules.Ftps.Interfaces;
using WiserTaskScheduler.Modules.Ftps.Models;
using WiserTaskScheduler.Modules.GenerateCommunications.Interfaces;
using WiserTaskScheduler.Modules.GenerateCommunications.Models;
using WiserTaskScheduler.Modules.SlackMessages.Interfaces;
using WiserTaskScheduler.Modules.SlackMessages.Models;

namespace WiserTaskScheduler.Core.Factories
{
    /// <summary>
    /// A factory to create the correct service for an action.
    /// </summary>
    public class ActionsServiceFactory : IActionsServiceFactory, IScopedService
    {
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Creates a new instance of <see cref="ActionsServiceFactory"/>.
        /// </summary>
        /// <param name="serviceProvider"></param>
        public ActionsServiceFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public IActionsService GetActionsServiceForAction(ActionModel action)
        {
            switch (action)
            {
                case QueryModel:
                    return serviceProvider.GetRequiredService<IQueriesService>() as IActionsService;
                case HttpApiModel:
                    return serviceProvider.GetRequiredService<IHttpApisService>() as IActionsService;
                case GenerateFileModel:
                    return serviceProvider.GetRequiredService<IGenerateFileService>() as IActionsService;
                case ImportFileModel:
                    return serviceProvider.GetRequiredService<IImportFilesService>() as IActionsService;
                case CleanupItemModel:
                    return serviceProvider.GetRequiredService<ICleanupItemsService>() as IActionsService;
                case BranchQueueModel:
                    return serviceProvider.GetRequiredService<IBranchQueueService>() as IActionsService;
                case WiserImportModel:
                    return serviceProvider.GetRequiredService<IWiserImportsService>() as IActionsService;
                case CommunicationModel:
                    return serviceProvider.GetRequiredService<ICommunicationsService>() as IActionsService;
                case ServerMonitorModel:
                    return serviceProvider.GetRequiredService<IServerMonitorsService>() as IActionsService;
                case FtpModel:
                    return serviceProvider.GetRequiredService<IFtpsService>() as IActionsService;
                case CleanupWiserHistoryModel:
                    return serviceProvider.GetRequiredService<ICleanupWiserHistoryService>() as IActionsService;
                case GenerateCommunicationModel:
                    return serviceProvider.GetRequiredService<IGenerateCommunicationsService>() as IActionsService;
                case DocumentStoreReadModel:
                    return serviceProvider.GetRequiredService<IDocumentStoreReadService>() as IActionsService;
                case SlackMessageModel:
                    return serviceProvider.GetRequiredService<ISlackMessageService>() as IActionsService;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action.ToString());
            }
        }
    }
}
