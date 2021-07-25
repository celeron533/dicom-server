﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Dicom.Core.Exceptions;
using Microsoft.Health.Dicom.Core.Features.ExtendedQueryTag;
using Microsoft.Health.Dicom.SqlServer.Features.Schema;
using Microsoft.Health.Dicom.SqlServer.Features.Schema.Model;
using Microsoft.Health.SqlServer.Features.Client;
using Microsoft.Health.SqlServer.Features.Storage;

namespace Microsoft.Health.Dicom.SqlServer.Features.ExtendedQueryTag
{
    internal class SqlExtendedQueryTagStoreV4 : SqlExtendedQueryTagStoreV3
    {
        public SqlExtendedQueryTagStoreV4(
           SqlConnectionWrapperFactory sqlConnectionWrapperFactory,
           ILogger<SqlExtendedQueryTagStoreV4> logger)
            : base(sqlConnectionWrapperFactory, logger)
        {
        }

        public override SchemaVersion Version => SchemaVersion.V4;

        public override async Task<IReadOnlyList<ExtendedQueryTagStoreEntry>> GetExtendedQueryTagsByOperationAsync(string operationId, CancellationToken cancellationToken = default)
        {
            EnsureArg.IsNotNullOrWhiteSpace(operationId, nameof(operationId));

            var results = new List<ExtendedQueryTagStoreEntry>();

            using (SqlConnectionWrapper sqlConnectionWrapper = await ConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken))
            using (SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand())
            {
                VLatest.GetExtendedQueryTagsByOperation.PopulateCommand(sqlCommandWrapper, operationId);

                using (SqlDataReader reader = await sqlCommandWrapper.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        (int tagKey, string tagPath, string tagVR, string tagPrivateCreator, int tagLevel, int tagStatus) = reader.ReadRow(
                            VLatest.ExtendedQueryTag.TagKey,
                            VLatest.ExtendedQueryTag.TagPath,
                            VLatest.ExtendedQueryTag.TagVR,
                            VLatest.ExtendedQueryTag.TagPrivateCreator,
                            VLatest.ExtendedQueryTag.TagLevel,
                            VLatest.ExtendedQueryTag.TagStatus);

                        results.Add(new ExtendedQueryTagStoreEntry(tagKey, tagPath, tagVR, tagPrivateCreator, (QueryTagLevel)tagLevel, (ExtendedQueryTagStatus)tagStatus));
                    }
                }
            }

            return results;
        }

        public override async Task<IReadOnlyList<int>> AddExtendedQueryTagsAsync(
            IEnumerable<AddExtendedQueryTagEntry> extendedQueryTagEntries,
            int maxAllowedCount,
            bool ready = false,
            CancellationToken cancellationToken = default)
        {
            EnsureArg.IsNotNull(extendedQueryTagEntries, nameof(extendedQueryTagEntries));
            EnsureArg.IsGt(maxAllowedCount, 0, nameof(maxAllowedCount));

            using SqlConnectionWrapper sqlConnectionWrapper = await ConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken);
            using SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand();

            IEnumerable<AddExtendedQueryTagsInputTableTypeV1Row> rows = extendedQueryTagEntries.Select(ToAddExtendedQueryTagsInputTableTypeV1Row);
            VLatest.AddExtendedQueryTags.PopulateCommand(sqlCommandWrapper, maxAllowedCount, ready, new VLatest.AddExtendedQueryTagsTableValuedParameters(rows));

            try
            {
                var keys = new List<int>();
                using SqlDataReader reader = await sqlCommandWrapper.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    keys.Add(reader.ReadRow(VLatest.ExtendedQueryTagString.TagKey));
                }

                return keys;
            }
            catch (SqlException ex)
            {
                throw ex.Number switch
                {
                    SqlErrorCodes.Conflict => ex.State == 1
                        ? new ExtendedQueryTagsExceedsMaxAllowedCountException(maxAllowedCount)
                        : new ExtendedQueryTagsAlreadyExistsException(),
                    _ => new DataStoreException(ex),
                };
            }
        }

        public override async Task<IReadOnlyList<ExtendedQueryTagStoreEntry>> AssignReindexingOperationAsync(
            IReadOnlyList<int> queryTagKeys,
            string operationId,
            bool returnIfCompleted = false,
            CancellationToken cancellationToken = default)
        {
            EnsureArg.HasItems(queryTagKeys, nameof(queryTagKeys));
            EnsureArg.IsNotNullOrWhiteSpace(operationId, nameof(operationId));

            using SqlConnectionWrapper sqlConnectionWrapper = await ConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken);
            using SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand();

            IEnumerable<ExtendedQueryTagKeyTableTypeV1Row> rows = queryTagKeys.Select(x => new ExtendedQueryTagKeyTableTypeV1Row(x));
            VLatest.AssignReindexingOperation.PopulateCommand(sqlCommandWrapper, rows, operationId, returnIfCompleted);

            try
            {
                var queryTags = new List<ExtendedQueryTagStoreEntry>();
                using SqlDataReader reader = await sqlCommandWrapper.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    (int tagKey, string tagPath, string tagVR, string tagPrivateCreator, byte tagLevel, byte tagStatus) = reader.ReadRow(
                        VLatest.ExtendedQueryTag.TagKey,
                        VLatest.ExtendedQueryTag.TagPath,
                        VLatest.ExtendedQueryTag.TagVR,
                        VLatest.ExtendedQueryTag.TagPrivateCreator,
                        VLatest.ExtendedQueryTag.TagLevel,
                        VLatest.ExtendedQueryTag.TagStatus);

                    queryTags.Add(new ExtendedQueryTagStoreEntry(
                        tagKey,
                        tagPath,
                        tagVR,
                        tagPrivateCreator,
                        (QueryTagLevel)tagLevel,
                        (ExtendedQueryTagStatus)tagStatus));
                }

                return queryTags;
            }
            catch (SqlException ex)
            {
                throw new DataStoreException(ex);
            }
        }

        public override async Task<IReadOnlyList<int>> CompleteReindexingAsync(IReadOnlyList<int> queryTagKeys, CancellationToken cancellationToken = default)
        {
            EnsureArg.HasItems(queryTagKeys, nameof(queryTagKeys));

            using SqlConnectionWrapper sqlConnectionWrapper = await ConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken);
            using SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand();

            IEnumerable<ExtendedQueryTagKeyTableTypeV1Row> rows = queryTagKeys.Select(x => new ExtendedQueryTagKeyTableTypeV1Row(x));
            VLatest.CompleteReindexing.PopulateCommand(sqlCommandWrapper, rows);

            try
            {
                var keys = new List<int>();
                using SqlDataReader reader = await sqlCommandWrapper.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    keys.Add(reader.ReadRow(VLatest.ExtendedQueryTagString.TagKey));
                }

                return keys;
            }
            catch (SqlException ex)
            {
                throw new DataStoreException(ex);
            }
        }

        public override async Task<IReadOnlyList<ExtendedQueryTagError>> GetExtendedQueryTagErrorsAsync(string tagPath, CancellationToken cancellationToken = default)
        {
            List<ExtendedQueryTagError> results = new List<ExtendedQueryTagError>();

            using SqlConnectionWrapper sqlConnectionWrapper = await ConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken);
            using SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand();

            //VLatest.GetExtendedQueryTagErrors.PopulateCommand(sqlCommandWrapper, tagPath);

            using SqlDataReader reader = await sqlCommandWrapper.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                (int tagkey, DateTime timestamp, int errorCode, string studyInstanceUid, string seriesInstanceUid, string sopInstanceUid, long sopinstanceKey) = reader.ReadRow(
                    VLatest.ExtendedQueryTagError.TagKey,
                    VLatest.ExtendedQueryTagError.CreatedTime,
                    VLatest.ExtendedQueryTagError.ErrorCode,
                    VLatest.ExtendedQueryTagError.StudyInstanceUid,
                    VLatest.ExtendedQueryTagError.SeriesInstanceUid,
                    VLatest.ExtendedQueryTagError.SopInstanceUid,
                    VLatest.ExtendedQueryTagError.SopInstanceKey
                    );

                //TODO: build the error message here
                string errorMessage = "error" + errorCode;

                results.Add(new ExtendedQueryTagError(timestamp, studyInstanceUid, seriesInstanceUid, sopInstanceUid,
                    errorMessage));
            }

            return results;
        }

        // returns Tag Key
        public override async Task<int> AddExtendedQueryTagErrorAsync(
            int tagKey,
            DateTime createdTime,
            int errorCode,
            string studyInstanceUid,
            string seriesInstanceUid,
            string sopInstanceUid,
            Int64 sopInstanceKey,
            CancellationToken cancellationToken = default)
        {
            using SqlConnectionWrapper sqlConnectionWrapper = await ConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken);
            using SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand();
            VLatest.AddExtendedQueryTagError.PopulateCommand(
                sqlCommandWrapper,
                tagKey,
                createdTime,
                errorCode,
                studyInstanceUid,
                seriesInstanceUid,
                sopInstanceUid,
                sopInstanceKey);
            try
            {
                return (int)await sqlCommandWrapper.ExecuteScalarAsync(cancellationToken);
            }
            catch (SqlException e)
            {
                switch (e.Number)
                {
                    case SqlErrorCodes.Conflict:
                        throw new ExtendedQueryTagErrorAlreadyExistsException();
                    case SqlErrorCodes.NotFound:
                        throw new ExtendedQueryTagNotFoundException("Attempted to add error on non existing query tag.");
                }

                throw new DataStoreException(e);
            }
        }

        public override async Task<bool> DeleteExtendedQueryTagErrorsAsync(string tagPath, CancellationToken cancellationToken = default)
        {
            using SqlConnectionWrapper sqlConnectionWrapper = await ConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken);
            using SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand();

            VLatest.DeleteExtendedQueryTagErrors.PopulateCommand(sqlCommandWrapper, tagPath);

            return await sqlCommandWrapper.ExecuteNonQueryAsync(cancellationToken) != 0;
        }
    }
}
