//! Parser for O2Jam (`.ojn`) chart binaries.

use encoding_rs::EUC_KR;

use crate::error::{Result, UrcError};

use super::model::{OjnDifficulty, OjnEvent, OjnFile};

const SIGNATURE: &[u8; 4] = b"ojn\x00";
const STRING_RANGES: [(usize, usize); 4] = [(108, 172), (172, 204), (204, 236), (236, 268)];

/// Parses a plain or encrypted (`new` magic) OJN file into its source model.
pub fn parse_ojn(bytes: &[u8]) -> Result<OjnFile> {
    let decrypted;
    let data: &[u8] = if bytes.len() >= 8 && &bytes[..3] == b"new" {
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
    if bpm <= 0.0 {
        return Err(UrcError::new("syntax", 16, "header BPM must be positive"));
    }

    let (title, artist, noter, _ojm) = decode_strings(data)?;
    let package_counts = [read_i32(data, 64), read_i32(data, 68), read_i32(data, 72)];
    let note_offsets = [
        read_i32(data, 284),
        read_i32(data, 288),
        read_i32(data, 292),
    ];
    let cover_offset = read_i32(data, 296);

    let mut difficulties = Vec::new();
    for index in 0..3 {
        if package_counts[index] == 0 {
            continue;
        }
        let start = note_offsets[index];
        let end_raw = if index < 2 {
            note_offsets[index + 1]
        } else {
            cover_offset
        };
        let end = (end_raw.max(0) as usize).min(data.len());
        difficulties.push(read_difficulty(
            data,
            index,
            start,
            end,
            package_counts[index],
        )?);
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
            start as u32,
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
        let total = read_i16(data, offset + 6);
        if measure < 0 {
            return Err(UrcError::new(
                "syntax",
                offset as u32,
                "negative measure index",
            ));
        }
        offset += 8;
        for i in 0..total.max(0) {
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
                if value <= 0.0 {
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

fn decode_strings(data: &[u8]) -> Result<(String, String, String, String)> {
    let fields: Vec<&[u8]> = STRING_RANGES
        .iter()
        .map(|&(start, end)| trim_nul(&data[start..end]))
        .collect();
    let blob: Vec<u8> = fields.concat();
    let is_ascii = blob.iter().all(|&byte| byte < 0x80);
    let charset = if is_ascii {
        "ascii"
    } else if std::str::from_utf8(&blob).is_ok() {
        "utf-8"
    } else if EUC_KR
        .decode_without_bom_handling_and_without_replacement(&blob)
        .is_some()
    {
        "cp949"
    } else {
        return Err(UrcError::new(
            "syntax",
            108,
            "strings are neither ASCII, UTF-8, nor CP949",
        ));
    };
    let mut decoded = Vec::with_capacity(4);
    for field in &fields {
        let text = if charset == "cp949" {
            EUC_KR
                .decode_without_bom_handling_and_without_replacement(field)
                .ok_or_else(|| UrcError::new("syntax", 108, "invalid string byte sequence"))?
                .into_owned()
        } else {
            let text = std::str::from_utf8(field)
                .map_err(|_| UrcError::new("syntax", 108, "invalid string byte sequence"))?;
            text.to_string()
        };
        decoded.push(text);
    }
    Ok((
        decoded[0].clone(),
        decoded[1].clone(),
        decoded[2].clone(),
        decoded[3].clone(),
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
