//! Structured errors returned while parsing URC documents.

/// Crate-wide result type for parsing operations.
pub type Result<T> = std::result::Result<T, UrcError>;

/// Parser failure with a machine-readable category and source line.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("{category} at line {line}: {message}")]
pub struct UrcError {
    /// `"syntax"`, `"unsupported-version"`, or `"rule:<n>"`.
    pub category: String,
    /// 1-based line where the failure was detected.
    pub line: u32,
    /// Human-readable detail.
    pub message: String,
}

impl UrcError {
    pub fn new(category: impl Into<String>, line: u32, message: impl Into<String>) -> Self {
        Self {
            category: category.into(),
            line,
            message: message.into(),
        }
    }
}
