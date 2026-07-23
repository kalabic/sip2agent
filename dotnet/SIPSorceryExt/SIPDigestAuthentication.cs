//-----------------------------------------------------------------------------
// Filename: SIPDigestAuthentication.cs
//
// Description: Builds authenticated SIP requests from precomputed HA1 values.
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

#nullable enable

using System.Collections.Generic;
using System.Linq;
using SIPSorcery.SIP;

namespace SIPSorceryExt;

internal delegate string? GetHA1DigestDelegate(
    string username,
    string realm,
    DigestAlgorithmsEnum algorithm);

internal static class SIPDigestAuthentication
{
    internal static SIPRequest? DuplicateAndAuthenticate(
        SIPRequest request,
        List<SIPAuthenticationHeader> authenticationChallenges,
        string username,
        GetHA1DigestDelegate getHA1Digest)
    {
        foreach (DigestAlgorithmsEnum digestAlgorithm in
                 new[] { DigestAlgorithmsEnum.SHA256, DigestAlgorithmsEnum.MD5 })
        {
            foreach (SIPAuthenticationHeader authenticationChallenge in authenticationChallenges.Where(
                         challenge => challenge.SIPDigest != null &&
                                      challenge.SIPDigest.DigestAlgorithm == digestAlgorithm))
            {
                SIPAuthorisationDigest challenge = authenticationChallenge.SIPDigest.CopyOf();
                string? ha1Digest = getHA1Digest(username, challenge.Realm, challenge.DigestAlgorithm);
                if (ha1Digest == null)
                {
                    continue;
                }

                challenge.Username = username;
                challenge.SetCredentials(ha1Digest, request.URI.ToString(), request.Method.ToString());

                var authenticationHeader = new SIPAuthenticationHeader(challenge);
                authenticationHeader.SIPDigest.Response = challenge.GetDigest();

                SIPRequest duplicateRequest = request.Copy();
                duplicateRequest.Header.Vias.TopViaHeader.Branch = CallProperties.CreateBranchId();
                duplicateRequest.Header.CSeq++;
                duplicateRequest.Header.AuthenticationHeaders.Clear();
                duplicateRequest.Header.AuthenticationHeaders.Add(authenticationHeader);
                return duplicateRequest;
            }
        }

        return null;
    }
}
