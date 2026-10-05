package org.kenjinx.android.cheats

internal object ModFolderRules {
    val payloadDirectories = setOf("romfs", "exefs", "cheats")
    val payloadFiles = setOf("romfs.bin", "exefs.nsp")

    fun isSafeName(name: String): Boolean = name.isNotBlank() && name != "." && name != ".." &&
        name.none { it == '/' || it == '\\' || it == '\u0000' }
}
