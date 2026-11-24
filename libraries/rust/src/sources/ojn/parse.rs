//! Parser for O2Jam (`.ojn`) chart binaries.

use encoding_rs::EUC_KR;

use crate::error::{Result, UrcError};

use super::model::{OjnDifficulty, OjnEvent, OjnFile};

const SIGNATURE: &[u8; 4] = b"ojn\x00";
const STRING_RANGES: [(usize, usize); 3] = [(108, 172), (172, 204), (204, 236)];

/// Parses a plain or encrypted (`new` magic) OJN file into its source model.
pub fn parse_ojn(bytes: &[u8]) -> Result<OjnFile> {
    let decrypted;
    let data: &[u8] = if bytes.len() >= 8 && &bytes[..3] == b"new" && &bytes[4..8] != SIGNATURE {
        decrypted = decrypt(bytes);
        &decrypted
    } else {
        bytes
    };
    if data.len() < 300 {
        return Err(UrcError::new(
            "syntax",
            data.len() as u32,
            "file too short for OJN header",
        ));
    }
    if &data[4..8] != SIGNATURE {
        return Err(UrcError::new("syntax", 4, "invalid OJN signature"));
    }
    let bpm = read_f32(data, 16);
    if !bpm.is_finite() || bpm <= 0.0 {
        return Err(UrcError::new("syntax", 16, "header BPM must be positive"));
    }

    let (title, artist, noter) = decode_strings(data)?;
    let package_counts = [read_i32(data, 64), read_i32(data, 68), read_i32(data, 72)];
    let note_offsets = [
        read_i32(data, 284),
        read_i32(data, 288),
        read_i32(data, 292),
    ];
    let cover_offset = read_i32(data, 296);

    let mut difficulties = Vec::new();
    for index in 0..3 {
        let count = package_counts[index];
        if count < 0 {
            return Err(UrcError::new(
                "syntax",
                (64 + index * 4) as u32,
                "negative package count",
            ));
        }
        if count == 0 {
            continue;
        }
        let start = note_offsets[index];
        let end = section_end(start, note_offsets, cover_offset, data.len());
        difficulties.push(read_difficulty(data, index, start, end, count)?);
    }
    if difficulties.is_empty() {
        return Err(UrcError::new(
            "syntax",
            0,
            "no difficulty with note packages",
        ));
    }
    Ok(OjnFile {
        title,
        artist,
        noter,
        bpm,
        difficulties,
    })
}

fn section_end(start: i32, note_offsets: [i32; 3], cover_offset: i32, file_len: usize) -> usize {
    let mut bound = file_len;
    for candidate in [note_offsets[0], note_offsets[1], note_offsets[2], cover_offset] {
        if candidate > start {
            bound = bound.min(candidate as usize);
        }
    }
    bound.min(file_len)
}

fn decrypt(data: &[u8]) -> Vec<u8> {
    let block = data[3] as usize;
    if block == 0 {
        return data.to_vec();
    }
    let mut key = vec![data[4]; block];
    key[0] = data[6];
    key[block / 2] = data[5];
    let size = data.len();
    (0..size - 8)
        .map(|i| data[size - 1 - i] ^ key[i % block])
        .collect()
}

fn read_difficulty(
    data: &[u8],
    index: usize,
    start: i32,
    end: usize,
    count: i32,
) -> Result<OjnDifficulty> {
    if start < 300 {
        return Err(UrcError::new(
            "syntax",
            start.max(0) as u32,
            "note section overlaps the header",
        ));
    }
    let mut events = Vec::new();
    let mut offset = start as usize;
    for _ in 0..count {
        if offset + 8 > end {
            return Err(UrcError::new(
                "syntax",
                offset as u32,
                "note section truncated",
            ));
        }
        let measure = read_i32(data, offset);
        let channel = read_u16(data, offset + 4);
        let total = read_u16(data, offset + 6);
        if measure < 0 {
            return Err(UrcError::new(
                "syntax",
                offset as u32,
                "negative measure index",
            ));
        }
        offset += 8;
        for i in 0..total {
            let position = i as f64 / total as f64;
            if offset + 4 > end {
                return Err(UrcError::new(
                    "syntax",
                    offset as u32,
                    "note section truncated",
                ));
            }
            if channel <= 1 {
                let value = read_f32(data, offset);
                offset += 4;
                if !value.is_finite() || value <= 0.0 {
                    continue;
                }
                events.push(OjnEvent {
                    measure,
                    position,
                    channel,
                    value,
                    kind: 0,
                    offset: (offset - 4) as u32,
                });
            } else {
                let value = read_i16(data, offset);
                let note_type = data[offset + 3];
                offset += 4;
                if channel >= 9 || value == 0 || note_type % 4 == 1 {
                    continue;
                }
                events.push(OjnEvent {
                    measure,
                    position,
                    channel,
                    value: 0.0,
                    kind: note_type % 4,
                    offset: (offset - 4) as u32,
                });
            }
        }
    }
    Ok(OjnDifficulty { index, events })
}

fn decode_strings(data: &[u8]) -> Result<(String, String, String)> {
    let title = decode_field(trim_nul(&data[STRING_RANGES[0].0..STRING_RANGES[0].1]))?;
    let artist = decode_field(trim_nul(&data[STRING_RANGES[1].0..STRING_RANGES[1].1]))?;
    let noter = decode_field(trim_nul(&data[STRING_RANGES[2].0..STRING_RANGES[2].1]))?;
    Ok((title, artist, noter))
}

fn decode_field(field: &[u8]) -> Result<String> {
    if let Ok(text) = std::str::from_utf8(field) {
        return Ok(text.to_string());
    }
    if let Some(decoded) = EUC_KR.decode_without_bom_handling_and_without_replacement(field) {
        return Ok(decoded.into_owned());
    }
    Err(UrcError::new(
        "syntax",
        108,
        "undecodable OJN metadata string",
    ))
}

fn trim_nul(field: &[u8]) -> &[u8] {
    let end = field
        .iter()
        .position(|&byte| byte == 0)
        .unwrap_or(field.len());
    &field[..end]
}

fn read_i32(data: &[u8], offset: usize) -> i32 {
    i32::from_le_bytes(data[offset..offset + 4].try_into().unwrap())
}

fn read_u16(data: &[u8], offset: usize) -> u16 {
    u16::from_le_bytes(data[offset..offset + 2].try_into().unwrap())
}

fn read_i16(data: &[u8], offset: usize) -> i16 {
    i16::from_le_bytes(data[offset..offset + 2].try_into().unwrap())
}

fn read_f32(data: &[u8], offset: usize) -> f64 {
    f32::from_le_bytes(data[offset..offset + 4].try_into().unwrap()) as f64
}
