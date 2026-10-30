// Relic: no metadata dump of the 3.2 client exists (its global-metadata.dat is a container the local Il2CppDumper
// forks do not read), so there is nothing to pin the layouts against here: the 3.2 layouts are upstream's
// (ee9258a), and code that needs a type or field they lack is compiled out for 3.2 rather than given a guessed one.
#pragma once
