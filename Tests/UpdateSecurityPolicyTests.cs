using System;
using System.Collections.Generic;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class UpdateSecurityPolicyTests
{
    [Fact]
    public void FromEnvironment_PreservesInternalSpacesInPublishers_AndTrims()
    {
        const string publishersVar = "VNOTCH_UPDATER_ALLOWED_PUBLISHERS";
        var original = Environment.GetEnvironmentVariable(publishersVar);

        try
        {
            Environment.SetEnvironmentVariable(publishersVar, "  Example Software Ltd ; Another Corporation   ");
            var policy = UpdateSecurityPolicy.FromEnvironment();

            Assert.Contains("Example Software Ltd", policy.AllowedPublisherNames);
            Assert.Contains("example software ltd", policy.AllowedPublisherNames); // case-insensitive
            Assert.Contains("Another Corporation", policy.AllowedPublisherNames);
            Assert.DoesNotContain("EXAMPLESOFTWARELTD", policy.AllowedPublisherNames); // internal spaces must not be stripped
        }
        finally
        {
            Environment.SetEnvironmentVariable(publishersVar, original);
        }
    }

    [Fact]
    public void FromEnvironment_NormalizesThumbprints_RemovesSpacesAndUpperCases()
    {
        const string thumbprintsVar = "VNOTCH_UPDATER_ALLOWED_THUMBPRINTS";
        var original = Environment.GetEnvironmentVariable(thumbprintsVar);

        try
        {
            Environment.SetEnvironmentVariable(thumbprintsVar, "  aa bb cc dd ; 11 22 33 44  ");
            var policy = UpdateSecurityPolicy.FromEnvironment();

            Assert.Contains("AABBCCDD", policy.AllowedCertificateThumbprints);
            Assert.Contains("aabbccdd", policy.AllowedCertificateThumbprints); // case-insensitive
            Assert.Contains("11223344", policy.AllowedCertificateThumbprints);
        }
        finally
        {
            Environment.SetEnvironmentVariable(thumbprintsVar, original);
        }
    }

    [Fact]
    public void AllowedPublisherNames_Init_TrimsAndPreservesInternalSpaces_CaseInsensitive()
    {
        var policy = new UpdateSecurityPolicy
        {
            AllowedPublisherNames = new HashSet<string> { "   V-Notch Developer Team   ", "  OpenSource Contributors  " }
        };

        Assert.Contains("V-Notch Developer Team", policy.AllowedPublisherNames);
        Assert.Contains("v-notch developer team", policy.AllowedPublisherNames);
        Assert.Contains("V-NOTCH DEVELOPER TEAM", policy.AllowedPublisherNames);
        Assert.DoesNotContain("V-NotchDeveloperTeam", policy.AllowedPublisherNames);
        Assert.Contains("OpenSource Contributors", policy.AllowedPublisherNames);
    }

    [Fact]
    public void AllowedCertificateThumbprints_Init_NormalizesSpaces_CaseInsensitive()
    {
        var policy = new UpdateSecurityPolicy
        {
            AllowedCertificateThumbprints = new HashSet<string> { " 01 23 45 67 89 ab cd ef " }
        };

        Assert.Contains("0123456789ABCDEF", policy.AllowedCertificateThumbprints);
        Assert.Contains("0123456789abcdef", policy.AllowedCertificateThumbprints);
    }

    [Fact]
    public void IsTrustedSignature_WhenAllowlistIncomplete_FailsWithReason()
    {
        var policyOnlyPublisher = new UpdateSecurityPolicy
        {
            AllowedPublisherNames = new HashSet<string> { "Example Software Ltd" }
        };

        bool trusted1 = policyOnlyPublisher.IsTrustedSignature("nonexistent.exe", out string reason1);
        Assert.False(trusted1);
        Assert.Contains("Authenticode allowlist is incomplete", reason1);

        var policyOnlyThumbprint = new UpdateSecurityPolicy
        {
            AllowedCertificateThumbprints = new HashSet<string> { "AABBCCDDEEFF" }
        };

        bool trusted2 = policyOnlyThumbprint.IsTrustedSignature("nonexistent.exe", out string reason2);
        Assert.False(trusted2);
        Assert.Contains("Authenticode allowlist is incomplete", reason2);
    }

    [Fact]
    public void IsTrustedSignature_WhenAllowlistEmpty_AllowsByDefault()
    {
        var policyEmpty = new UpdateSecurityPolicy();
        bool trusted = policyEmpty.IsTrustedSignature("nonexistent.exe", out string reason);
        Assert.True(trusted);
        Assert.Contains("No additional Authenticode policy configured", reason);
    }
}
