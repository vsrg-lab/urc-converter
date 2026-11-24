//! URC (Universal Rhythm Chart) parser and converter library.

pub mod error;
pub mod model;
pub mod parser;
pub mod rate;
pub mod sources;
pub mod strings;
pub mod writer;

pub use error::{Result, UrcError};
pub use model::{
    Chart, Judgment, Layout, Metadata, Meter, Note, NoteType, RateResult, TimingPoint, UrcWarning,
    Version, WarningCode,
};
pub use parser::scan::parse;
pub use rate::scale_rate;
pub use writer::write;
