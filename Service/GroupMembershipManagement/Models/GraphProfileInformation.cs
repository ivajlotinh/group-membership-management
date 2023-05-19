// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Diagnostics.CodeAnalysis;

namespace Models
{
    [ExcludeFromCodeCoverage]
    public class GraphProfileInformation: IEquatable<GraphProfileInformation>
    {
        /// <summary>
        /// Gets or sets the AAD Object Id of a Graph User
        /// </summary>
        public string Id { get; set; }
        /// <summary>
        /// Gets or sets the onPremisesImmutableId of a Graph User
        /// </summary>
        public string PersonnelNumber { get; set; }
        /// <summary>
        /// Gets or sets the userPrincipalName of a Graph User
        /// </summary>
        public string UserPrincipalName { get; set; }

        public bool Equals(GraphProfileInformation other)
        {
            return PersonnelNumber.Equals(other.PersonnelNumber);
        }

        public override int GetHashCode()
        {
            return PersonnelNumber.GetHashCode();
        }
    }
}
