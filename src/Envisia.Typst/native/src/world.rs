use std::collections::HashMap;

use typst::diag::{FileError, FileResult};
use typst::foundations::{Bytes, Datetime, Duration};
use typst::syntax::{FileId, RootedPath, Source, VirtualPath, VirtualRoot};
use typst::text::{Font, FontBook};
use typst::utils::LazyHash;
use typst::{Library, LibraryExt, World};

pub struct EnvisiaWorld {
    library: LazyHash<Library>,
    book: LazyHash<FontBook>,
    fonts: Vec<Font>,
    main: Source,
    files: HashMap<FileId, Bytes>,
    now: Option<(i32, u8, u8)>,
}

impl EnvisiaWorld {
    pub fn new(
        markup: String,
        font_data: Vec<Bytes>,
        files: Vec<(String, Bytes)>,
        now: Option<(i32, u8, u8)>,
    ) -> Result<Self, String> {
        let fonts: Vec<Font> = font_data.into_iter().flat_map(Font::iter).collect();
        let book = FontBook::from_fonts(&fonts);
        let main = Source::new(intern("main.typ")?, markup);

        let mut map = HashMap::with_capacity(files.len());
        for (name, bytes) in files {
            map.insert(intern(&name)?, bytes);
        }

        Ok(Self {
            library: LazyHash::new(Library::default()),
            book: LazyHash::new(book),
            fonts,
            main,
            files: map,
            now,
        })
    }

    pub fn font_count(&self) -> usize {
        self.fonts.len()
    }
}

fn intern(name: &str) -> Result<FileId, String> {
    let vpath =
        VirtualPath::new(name).map_err(|error| format!("invalid file name '{name}': {error}"))?;
    Ok(RootedPath::new(VirtualRoot::Project, vpath).intern())
}

impl World for EnvisiaWorld {
    fn library(&self) -> &LazyHash<Library> {
        &self.library
    }

    fn book(&self) -> &LazyHash<FontBook> {
        &self.book
    }

    fn main(&self) -> FileId {
        self.main.id()
    }

    fn source(&self, id: FileId) -> FileResult<Source> {
        if id == self.main.id() {
            return Ok(self.main.clone());
        }

        match self.files.get(&id) {
            Some(bytes) => {
                let text = std::str::from_utf8(bytes)
                    .map_err(|_| FileError::InvalidUtf8)?
                    .to_owned();
                Ok(Source::new(id, text))
            }
            None => Err(not_found(id)),
        }
    }

    fn file(&self, id: FileId) -> FileResult<Bytes> {
        match self.files.get(&id) {
            Some(bytes) => Ok(bytes.clone()),
            None => Err(not_found(id)),
        }
    }

    fn font(&self, index: usize) -> Option<Font> {
        self.fonts.get(index).cloned()
    }

    fn today(&self, _offset: Option<Duration>) -> Option<Datetime> {
        let (year, month, day) = self.now?;
        Datetime::from_ymd(year, month, day)
    }
}

fn not_found(id: FileId) -> FileError {
    FileError::NotFound(id.vpath().get_with_slash().into())
}
