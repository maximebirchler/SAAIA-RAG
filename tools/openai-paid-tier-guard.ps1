function Assert-OpenAiPaidTierObservation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ObservedOrganizationTier,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$TierObservedAtUtc,
        [ValidateRange(1, 1440)]
        [int]$MaximumAgeMinutes = 15
    )

    if ($ObservedOrganizationTier -notmatch '^Tier[1-5]$') {
        throw "OpenAI live model calls require a freshly verified paid tier (Tier1 through Tier5). Free-tier calls are blocked."
    }
    if ([string]::IsNullOrWhiteSpace($TierObservedAtUtc)) {
        throw "TierObservedAtUtc is required for OpenAI live model calls."
    }
    if ($TierObservedAtUtc -notmatch '(?:[zZ]|[+-]\d{2}:\d{2})$') {
        throw "TierObservedAtUtc must include an explicit UTC or numeric offset."
    }
    try {
        $observedAt = [DateTimeOffset]::Parse(
            $TierObservedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "TierObservedAtUtc must be a valid timestamp with an explicit offset."
    }
    $ageMinutes = ([DateTimeOffset]::UtcNow - $observedAt).TotalMinutes
    if ($ageMinutes -lt -2 -or $ageMinutes -gt $MaximumAgeMinutes) {
        throw "The OpenAI paid-tier observation is missing, stale or implausibly in the future. Verify the organization tier again."
    }
    return $observedAt
}
