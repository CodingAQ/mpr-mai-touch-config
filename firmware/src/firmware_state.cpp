#include "firmware_state.h"

#include "touchmap.h"

namespace firmware_state {

uint8_t statusForRequest(Mode mode, uint8_t requestType) {
  if (requestType == protocol::TypeVersion) return protocol::StatusOk;
  if (requestType == protocol::TypeExitConfig) {
    return mode == Mode::Game ? protocol::StatusOk : protocol::StatusOk;
  }
  if (mode == Mode::Game) return protocol::StatusOk;
  if (mode == Mode::Streaming) {
    return requestType == protocol::TypeStreamStop ? protocol::StatusOk
                                                     : protocol::StatusBusy;
  }
  if (mode == Mode::Calibrating) return protocol::StatusBusy;
  if (requestType == protocol::TypeStreamStop) return protocol::StatusIllegalState;
  return protocol::StatusOk;
}

int8_t electrodeOffsetForChannel(const config::Config& value, uint8_t board,
                                 uint8_t channel) {
  return config::electrodeOffsetForChannel(value, board, channel);
}

uint16_t thresholdForChannel(const config::Config& value, uint8_t board,
                             uint8_t channel) {
  return config::touchDelta(
      value, board, config::electrodeOffsetForChannel(value, board, channel));
}

SequenceDecision classifySequence(bool cacheValid, uint16_t sequence, uint8_t type,
                                  uint16_t payloadCrc, uint16_t cachedSequence,
                                  uint8_t cachedType, uint16_t cachedPayloadCrc) {
  if (!cacheValid || sequence != cachedSequence) return SequenceDecision::Execute;
  return type == cachedType && payloadCrc == cachedPayloadCrc
             ? SequenceDecision::Replay
             : SequenceDecision::Conflict;
}

bool encodeDebugPayload(uint32_t timestampMs, const bool* keys,
                        const uint16_t filtered[touchmap::ChannelCount],
                        const uint16_t baseline[touchmap::ChannelCount],
                        const uint8_t status[touchmap::BoardCount], uint16_t i2cErrors,
                        uint8_t* payload, uint16_t payloadCapacity) {
  if (keys == nullptr || filtered == nullptr || baseline == nullptr || status == nullptr ||
      payload == nullptr || payloadCapacity < DebugPayloadLength) {
    return false;
  }
  for (uint16_t i = 0; i < DebugPayloadLength; ++i) payload[i] = 0;
  payload[0] = static_cast<uint8_t>(timestampMs);
  payload[1] = static_cast<uint8_t>(timestampMs >> 8);
  payload[2] = static_cast<uint8_t>(timestampMs >> 16);
  payload[3] = static_cast<uint8_t>(timestampMs >> 24);
  const uint64_t bits = protocol::packKeyBits(keys);
  for (uint8_t i = 0; i < 5; ++i) payload[4 + i] = static_cast<uint8_t>(bits >> (i * 8));
  for (uint8_t i = 0; i < touchmap::ChannelCount; ++i) {
    payload[9 + i * 2] = static_cast<uint8_t>(filtered[i]);
    payload[10 + i * 2] = static_cast<uint8_t>(filtered[i] >> 8);
    payload[105 + i * 2] = static_cast<uint8_t>(baseline[i]);
    payload[106 + i * 2] = static_cast<uint8_t>(baseline[i] >> 8);
  }
  for (uint8_t i = 0; i < touchmap::BoardCount; ++i) payload[201 + i] = status[i];
  payload[205] = static_cast<uint8_t>(i2cErrors);
  payload[206] = static_cast<uint8_t>(i2cErrors >> 8);
  return true;
}

uint16_t maskedDebugPayloadSize(uint8_t fieldMask) {
  uint16_t size = 5;  // mask u8 + timestamp u32
  if (fieldMask & FieldMaskTouch) size += 5;
  if (fieldMask & FieldMaskFiltered) size += 96;  // 48 × u16
  if (fieldMask & FieldMaskBaseline) size += 48;  // 48 × u8 原始寄存器值
  if (fieldMask & FieldMaskStatus) size += 6;     // status 4B + i2c 2B
  if (fieldMask & FieldMaskExtended) size += 44;  // hw_touch 5 + thresholds 34 + active 5
  return size;
}

bool encodeMaskedDebugPayload(uint8_t fieldMask, uint32_t timestampMs, const bool* keys,
                              const uint16_t filtered[touchmap::ChannelCount],
                              const uint16_t baseline[touchmap::ChannelCount],
                              const uint8_t status[touchmap::BoardCount], uint16_t i2cErrors,
                              uint64_t hwTouchBits, const uint8_t thresholds[touchmap::KeyCount],
                              uint64_t activeBits, uint8_t* payload, uint16_t payloadCapacity) {
  // 0x1F 之外的位没有定义，一律拒编：产出一帧违反 §5.6 字段掩码规则的负载，
  // 比直接失败更难查——读方会按自己理解的布局解析出一堆错位数据。
  constexpr uint8_t KnownFieldMask = FieldMaskTouch | FieldMaskFiltered | FieldMaskBaseline |
                                     FieldMaskStatus | FieldMaskExtended;
  if (fieldMask == 0 || (fieldMask & ~KnownFieldMask) != 0 ||
      keys == nullptr || filtered == nullptr || baseline == nullptr ||
      status == nullptr || thresholds == nullptr || payload == nullptr ||
      payloadCapacity < maskedDebugPayloadSize(fieldMask)) {
    return false;
  }
  const uint16_t size = maskedDebugPayloadSize(fieldMask);
  for (uint16_t i = 0; i < size; ++i) payload[i] = 0;
  uint16_t offset = 0;
  payload[offset++] = fieldMask;
  payload[offset++] = static_cast<uint8_t>(timestampMs);
  payload[offset++] = static_cast<uint8_t>(timestampMs >> 8);
  payload[offset++] = static_cast<uint8_t>(timestampMs >> 16);
  payload[offset++] = static_cast<uint8_t>(timestampMs >> 24);
  if (fieldMask & FieldMaskTouch) {
    const uint64_t bits = protocol::packKeyBits(keys);
    for (uint8_t i = 0; i < 5; ++i) payload[offset + i] = static_cast<uint8_t>(bits >> (i * 8));
    offset += 5;
  }
  if (fieldMask & FieldMaskFiltered) {
    for (uint8_t i = 0; i < touchmap::ChannelCount; ++i) {
      payload[offset + i * 2] = static_cast<uint8_t>(filtered[i]);
      payload[offset + i * 2 + 1] = static_cast<uint8_t>(filtered[i] >> 8);
    }
    offset += 96;
  }
  if (fieldMask & FieldMaskBaseline) {
    // baseline 寄存器本身 8 位（u16 里低 2 位恒零），按原始寄存器值传，读方 <<2 还原。
    for (uint8_t i = 0; i < touchmap::ChannelCount; ++i) payload[offset + i] = static_cast<uint8_t>(baseline[i] >> 2);
    offset += 48;
  }
  if (fieldMask & FieldMaskStatus) {
    for (uint8_t i = 0; i < touchmap::BoardCount; ++i) payload[offset + i] = status[i];
    payload[offset + 4] = static_cast<uint8_t>(i2cErrors);
    payload[offset + 5] = static_cast<uint8_t>(i2cErrors >> 8);
    offset += 6;
  }
  if (fieldMask & FieldMaskExtended) {
    for (uint8_t i = 0; i < 5; ++i) payload[offset + i] = static_cast<uint8_t>(hwTouchBits >> (i * 8));
    offset += 5;
    for (uint8_t i = 0; i < touchmap::KeyCount; ++i) payload[offset + i] = thresholds[i];
    offset += touchmap::KeyCount;
    for (uint8_t i = 0; i < 5; ++i) payload[offset + i] = static_cast<uint8_t>(activeBits >> (i * 8));
    offset += 5;
  }
  return true;
}

}  // namespace firmware_state
