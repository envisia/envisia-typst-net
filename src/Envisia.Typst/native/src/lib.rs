mod world;

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use typst::diag::{Severity, SourceDiagnostic, Warned};
use typst::ecow::EcoVec;
use typst::foundations::{Bytes, Smart};
use typst::syntax::DiagSpan;
use typst::{World, WorldExt};
use typst_layout::PagedDocument;
use typst_pdf::{PdfOptions, PdfStandard, PdfStandards};

use crate::world::EnvisiaWorld;

pub const ABI_VERSION: u32 = 3;

pub const STATUS_OK: i32 = 0;
pub const STATUS_COMPILE_ERROR: i32 = 1;
pub const STATUS_INVALID_INPUT: i32 = 2;
pub const STATUS_PANIC: i32 = 3;

#[repr(C)]
pub struct EvBuffer {
    pub ptr: *const u8,
    pub len: usize,
}

#[repr(C)]
pub struct EvNamedBuffer {
    pub name: *const u8,
    pub name_len: usize,
    pub data: *const u8,
    pub data_len: usize,
}

#[repr(C)]
pub struct EvTypstResult {
    pub status: i32,
    pub pdf: *const u8,
    pub pdf_len: usize,
    pub message: *const u8,
    pub message_len: usize,
    handle: *mut ResultOwner,
}

struct ResultOwner {
    pdf: Vec<u8>,
    message: String,
}

impl EvTypstResult {
    fn empty(status: i32) -> Self {
        Self {
            status,
            pdf: ptr::null(),
            pdf_len: 0,
            message: ptr::null(),
            message_len: 0,
            handle: ptr::null_mut(),
        }
    }

    fn owned(status: i32, pdf: Vec<u8>, message: String) -> Self {
        let owner = Box::new(ResultOwner { pdf, message });
        Self {
            status,
            pdf: owner.pdf.as_ptr(),
            pdf_len: owner.pdf.len(),
            message: owner.message.as_ptr(),
            message_len: owner.message.len(),
            handle: Box::into_raw(owner),
        }
    }
}

#[no_mangle]
pub extern "C" fn envisia_typst_abi_version() -> u32 {
    ABI_VERSION
}

/// # Safety
/// `result` must point at a writable `EvTypstResult`. The slice arguments must either be null with a
/// zero count or point at `count` readable elements that stay alive for the duration of the call.
/// Every buffer referenced from an element must stay readable for the duration of the call. With `creator_set`
/// non-zero, `creator` must point at `creator_len` readable bytes (or be null with a zero length). `standards` is a
/// comma separated list of Typst's standard names (`a-3b`, `ua-1`, ...) in `standards_len` readable bytes, or null
/// with a zero length for none; `tagged` non-zero writes a tagged PDF.
#[no_mangle]
pub unsafe extern "C" fn envisia_typst_compile_pdf(
    markup: *const u8,
    markup_len: usize,
    fonts: *const EvBuffer,
    font_count: usize,
    files: *const EvNamedBuffer,
    file_count: usize,
    year: i32,
    month: u8,
    day: u8,
    creator: *const u8,
    creator_len: usize,
    creator_set: u8,
    standards: *const u8,
    standards_len: usize,
    tagged: u8,
    result: *mut EvTypstResult,
) -> i32 {
    if result.is_null() {
        return STATUS_INVALID_INPUT;
    }

    let outcome = catch_unwind(AssertUnwindSafe(|| {
        let markup = match read_str(markup, markup_len) {
            Some(value) => value,
            None => {
                return EvTypstResult::owned(
                    STATUS_INVALID_INPUT,
                    Vec::new(),
                    "markup is not valid UTF-8".to_owned(),
                )
            }
        };

        let fonts = match read_buffers(fonts, font_count) {
            Some(value) => value,
            None => {
                return EvTypstResult::owned(
                    STATUS_INVALID_INPUT,
                    Vec::new(),
                    "font list pointer is null".to_owned(),
                )
            }
        };

        if fonts.is_empty() {
            return EvTypstResult::owned(
                STATUS_INVALID_INPUT,
                Vec::new(),
                "at least one font must be supplied".to_owned(),
            );
        }

        let files = match read_named_buffers(files, file_count) {
            Some(value) => value,
            None => {
                return EvTypstResult::owned(
                    STATUS_INVALID_INPUT,
                    Vec::new(),
                    "file list pointer is null or a name is not valid UTF-8".to_owned(),
                )
            }
        };

        let creator = if creator_set == 0 {
            Smart::Auto
        } else {
            match read_str(creator, creator_len) {
                Some(value) => Smart::Custom(Some(value).filter(|value| !value.is_empty())),
                None => {
                    return EvTypstResult::owned(
                        STATUS_INVALID_INPUT,
                        Vec::new(),
                        "creator is not valid UTF-8".to_owned(),
                    )
                }
            }
        };

        let standards = match read_str(standards, standards_len) {
            Some(value) => value,
            None => {
                return EvTypstResult::owned(
                    STATUS_INVALID_INPUT,
                    Vec::new(),
                    "standards are not valid UTF-8".to_owned(),
                )
            }
        };

        let standards = match parse_standards(&standards) {
            Ok(value) => value,
            Err(message) => return EvTypstResult::owned(STATUS_INVALID_INPUT, Vec::new(), message),
        };

        let options = PdfOptions {
            creator,
            standards,
            tagged: tagged != 0,
            ..PdfOptions::default()
        };

        let now = if year > 0 {
            Some((year, month, day))
        } else {
            None
        };
        compile(markup, fonts, files, now, &options)
    }));

    let value = outcome.unwrap_or_else(|_| {
        EvTypstResult::owned(
            STATUS_PANIC,
            Vec::new(),
            "the typst compiler panicked".to_owned(),
        )
    });

    let status = value.status;
    ptr::write(result, value);
    status
}

/// # Safety
/// `result` must be a pointer previously filled by `envisia_typst_compile_pdf` and not yet freed.
#[no_mangle]
pub unsafe extern "C" fn envisia_typst_result_free(result: *mut EvTypstResult) {
    if result.is_null() {
        return;
    }

    let handle = (*result).handle;
    if !handle.is_null() {
        drop(Box::from_raw(handle));
    }

    ptr::write(result, EvTypstResult::empty(STATUS_OK));
}

fn compile(
    markup: String,
    fonts: Vec<Bytes>,
    files: Vec<(String, Bytes)>,
    now: Option<(i32, u8, u8)>,
    options: &PdfOptions,
) -> EvTypstResult {
    let world = match EnvisiaWorld::new(markup, fonts, files, now) {
        Ok(world) => world,
        Err(message) => return EvTypstResult::owned(STATUS_INVALID_INPUT, Vec::new(), message),
    };

    if world.font_count() == 0 {
        return EvTypstResult::owned(
            STATUS_INVALID_INPUT,
            Vec::new(),
            "none of the supplied font buffers could be parsed".to_owned(),
        );
    }

    let result = export(&world, options);

    // comemo's memoization cache is process global and never shrinks on its own. Each document is rendered once, so
    // nothing is kept for the next call: a large report otherwise stays resident until later calls age it out.
    // This must be the comemo version typst itself uses, another one has a cache of its own that typst never fills.
    comemo::evict(0);

    result
}

// Stops compiling once typst moves to another comemo than this crate, rather than evict clearing the wrong cache.
#[allow(dead_code)]
fn same_comemo_as_typst(world: &dyn World) -> comemo::Tracked<'_, dyn World + '_> {
    comemo::Track::track(world)
}

fn export(world: &EnvisiaWorld, options: &PdfOptions) -> EvTypstResult {
    let Warned { output, warnings } = typst::compile::<PagedDocument>(world);

    let document = match output {
        Ok(document) => document,
        Err(diagnostics) => {
            return EvTypstResult::owned(
                STATUS_COMPILE_ERROR,
                Vec::new(),
                format_diagnostics(world, &diagnostics),
            )
        }
    };

    match typst_pdf::pdf(&document, options) {
        Ok(bytes) => EvTypstResult::owned(STATUS_OK, bytes, format_diagnostics(world, &warnings)),
        Err(diagnostics) => EvTypstResult::owned(
            STATUS_COMPILE_ERROR,
            Vec::new(),
            format_diagnostics(world, &diagnostics),
        ),
    }
}

fn parse_standards(names: &str) -> Result<PdfStandards, String> {
    let mut list = Vec::new();
    for name in names
        .split(',')
        .map(str::trim)
        .filter(|name| !name.is_empty())
    {
        list.push(parse_standard(name).ok_or_else(|| format!("unknown pdf standard '{name}'"))?);
    }

    if list.is_empty() {
        return Ok(PdfStandards::default());
    }

    PdfStandards::new(&list).map_err(|error| {
        let mut message = error.message().to_string();
        for hint in error.hints() {
            message.push_str("\n  hint: ");
            message.push_str(hint);
        }
        message
    })
}

// The names of typst's `--pdf-standard` option, which TypstPdfStandard maps onto.
fn parse_standard(name: &str) -> Option<PdfStandard> {
    Some(match name {
        "1.4" => PdfStandard::V_1_4,
        "1.5" => PdfStandard::V_1_5,
        "1.6" => PdfStandard::V_1_6,
        "1.7" => PdfStandard::V_1_7,
        "2.0" => PdfStandard::V_2_0,
        "a-1b" => PdfStandard::A_1b,
        "a-1a" => PdfStandard::A_1a,
        "a-2b" => PdfStandard::A_2b,
        "a-2u" => PdfStandard::A_2u,
        "a-2a" => PdfStandard::A_2a,
        "a-3b" => PdfStandard::A_3b,
        "a-3u" => PdfStandard::A_3u,
        "a-3a" => PdfStandard::A_3a,
        "a-4" => PdfStandard::A_4,
        "a-4f" => PdfStandard::A_4f,
        "a-4e" => PdfStandard::A_4e,
        "ua-1" => PdfStandard::Ua_1,
        _ => return None,
    })
}

fn format_diagnostics(world: &EnvisiaWorld, diagnostics: &EcoVec<SourceDiagnostic>) -> String {
    let mut out = String::new();
    for diagnostic in diagnostics {
        if !out.is_empty() {
            out.push('\n');
        }

        out.push_str(match diagnostic.severity {
            Severity::Error => "error",
            Severity::Warning => "warning",
        });

        if let Some(location) = describe_span(world, diagnostic.span) {
            out.push_str(&format!(" [{location}]"));
        }

        out.push_str(": ");
        out.push_str(&diagnostic.message);

        for hint in &diagnostic.hints {
            out.push_str("\n  hint: ");
            out.push_str(&hint.v);
        }

        for point in &diagnostic.trace {
            out.push_str("\n  in: ");
            out.push_str(&point.v.to_string());
        }
    }

    out
}

fn describe_span(world: &EnvisiaWorld, span: DiagSpan) -> Option<String> {
    let id = span.id()?;
    let range = world.range(span)?;
    let source = world.source(id).ok()?;
    let (line, column) = source.lines().byte_to_line_column(range.start)?;
    Some(format!(
        "{}:{}:{}",
        id.vpath().get_with_slash(),
        line + 1,
        column + 1
    ))
}

unsafe fn read_str(ptr: *const u8, len: usize) -> Option<String> {
    if len == 0 {
        return Some(String::new());
    }

    if ptr.is_null() {
        return None;
    }

    std::str::from_utf8(std::slice::from_raw_parts(ptr, len))
        .ok()
        .map(|value| value.to_owned())
}

unsafe fn read_buffers(ptr: *const EvBuffer, count: usize) -> Option<Vec<Bytes>> {
    if count == 0 {
        return Some(Vec::new());
    }

    if ptr.is_null() {
        return None;
    }

    let mut out = Vec::with_capacity(count);
    for entry in std::slice::from_raw_parts(ptr, count) {
        if entry.ptr.is_null() || entry.len == 0 {
            continue;
        }

        out.push(Bytes::new(
            std::slice::from_raw_parts(entry.ptr, entry.len).to_vec(),
        ));
    }

    Some(out)
}

unsafe fn read_named_buffers(
    ptr: *const EvNamedBuffer,
    count: usize,
) -> Option<Vec<(String, Bytes)>> {
    if count == 0 {
        return Some(Vec::new());
    }

    if ptr.is_null() {
        return None;
    }

    let mut out = Vec::with_capacity(count);
    for entry in std::slice::from_raw_parts(ptr, count) {
        let name = read_str(entry.name, entry.name_len)?;
        if name.is_empty() {
            return None;
        }

        let data = if entry.data.is_null() || entry.data_len == 0 {
            Vec::new()
        } else {
            std::slice::from_raw_parts(entry.data, entry.data_len).to_vec()
        };

        out.push((name, Bytes::new(data)));
    }

    Some(out)
}
