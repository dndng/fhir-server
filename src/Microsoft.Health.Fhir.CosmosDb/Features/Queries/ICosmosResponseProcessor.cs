﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Queries
{
    public interface ICosmosResponseProcessor
    {
        Task ProcessErrorResponse(ResponseMessage ex);

        Task ProcessResponse(string sessionToken, double responseRequestCharge, HttpStatusCode? statusCode);
    }
}
