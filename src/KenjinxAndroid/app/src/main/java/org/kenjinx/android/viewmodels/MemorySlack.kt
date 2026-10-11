package org.kenjinx.android.viewmodels

enum class MemorySlack(val gigabytes: Int) {
    None(0),
    Slack4GiB(4),
    Slack8GiB(8),
    Slack12GiB(12);
}