# AI Notes

AI was used to speed up scaffolding, test planning, and to cross-check the likely example input/output shape from the public assignment prompt.

One concrete example of changing AI output: an early suggestion treated multiple `F` records under one person as duplicates to ignore. I rejected that because the assignment's public Victoria Bernadotte example clearly contains two family members, so the final parser keeps multiple `F` blocks and only treats singleton `D`, `T`, and `A` blocks as duplicates.
