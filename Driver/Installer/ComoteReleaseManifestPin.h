#pragma once

#include <array>
#include <cstdint>

// This checked-in header is deliberately unusable for release installation.
// Build-ComoteReleasePinnedInstaller.ps1 replaces it only after validating and
// hashing the exact final release manifest.  The installer must fail closed
// whenever kPinned is false.
namespace comote::release_manifest_pin
{
inline constexpr bool kPinned = false;
inline constexpr char kState[] = "UNPINNED-INSTALL-MUST-REJECT";
inline constexpr char kSha256Hex[] =
    "0000000000000000000000000000000000000000000000000000000000000000";
inline constexpr std::array<std::uint8_t, 32> kSha256 = {
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
} // namespace comote::release_manifest_pin
