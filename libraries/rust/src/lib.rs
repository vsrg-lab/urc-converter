//! URC (Universal Rhythm Chart) parser and converter library.

pub mod error;
pub mod model;
pub mod parser;
pub mod writer;

pub use error::{Result, UrcError};
pub use model::{Chart, Judgment, Layout, Metadata, Meter, Note, NoteType, TimingPoint, Version};
pub use parser::parse;
pub use writer::write;
