#pragma once

#include <stdint.h>

#include "config.h"
#include "protocol.h"

namespace firmware_state {

enum class Mode : uint8_t { Game = 0, ConfigIdle = 1, Streaming = 2, Calibrating = 3 };
enum class SequenceDecision : uint8_t { Execute, Replay, Conflict };

constexpr uint32_t VersionCapabilities = 0x7FUL;  // bit6 (0x40)：录制掩码模式（协议 1.3）
constexpr uint16_t ExtendedDebugPayloadLength = 251;
constexpr uint16_t DebugPayloadLength = 207;

// 0x91 掩码调试帧的 field_mask 位定义（与工具侧 .mtr 存储记录一致）。
constexpr uint8_t FieldMaskTouch = 0x01;
constexpr uint8_t FieldMaskFiltered = 0x02;
constexpr uint8_t FieldMaskBaseline = 0x04;
constexpr uint8_t FieldMaskStatus = 0x08;
constexpr uint8_t FieldMaskExtended = 0x10;  // 44B：hw_touch 5 + comp_thresholds 34 + comp_active 5
constexpr uint16_t MaxMaskedDebugPayloadLength = 5 + 5 + 96 + 48 + 6 + 44;  // 全字段 204

// 按 field_mask 计算 0x91 负载长度：mask u8 + ts u32 + 各可选字段。
uint16_t maskedDebugPayloadSize(uint8_t fieldMask);

// 掩码调试帧（0x91）负载编码。布局镜像 .mtr 存储记录（无主机接收时刻）：
//   mask u8 · timestamp u32 · [touch 5B] · [filtered 48×u16] · [baseline 48×u8 原始寄存器值] ·
//   [status 4B · i2c 2B] · [extended 44B]
// extended 仅在 fieldMask 含 bit4 时写入；调用方保证此时 hwTouchBits/thresholds/activeBits 有效。
bool encodeMaskedDebugPayload(uint8_t fieldMask, uint32_t timestampMs, const bool* keys,
                              const uint16_t filtered[touchmap::ChannelCount],
                              const uint16_t baseline[touchmap::ChannelCount],
                              const uint8_t status[touchmap::BoardCount], uint16_t i2cErrors,
                              uint64_t hwTouchBits, const uint8_t thresholds[touchmap::KeyCount],
                              uint64_t activeBits, uint8_t* payload, uint16_t payloadCapacity);

uint8_t statusForRequest(Mode mode, uint8_t requestType);
int8_t electrodeOffsetForChannel(const config::Config& value, uint8_t board,
                                 uint8_t channel);
uint16_t thresholdForChannel(const config::Config& value, uint8_t board,
                             uint8_t channel);
SequenceDecision classifySequence(bool cacheValid, uint16_t sequence, uint8_t type,
                                  uint16_t payloadCrc, uint16_t cachedSequence,
                                  uint8_t cachedType, uint16_t cachedPayloadCrc);
bool encodeDebugPayload(uint32_t timestampMs, const bool* keys,
                        const uint16_t filtered[touchmap::ChannelCount],
                        const uint16_t baseline[touchmap::ChannelCount],
                        const uint8_t status[touchmap::BoardCount], uint16_t i2cErrors,
                        uint8_t* payload, uint16_t payloadCapacity);

}  // namespace firmware_state
