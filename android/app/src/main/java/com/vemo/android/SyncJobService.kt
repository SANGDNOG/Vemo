package com.vemo.android

import android.app.job.*
import android.content.ComponentName
import android.content.Context
import com.vemo.android.core.Mode

class SyncJobService : JobService() {
    @Volatile private var stopped = false
    override fun onStartJob(params: JobParameters): Boolean {
        stopped = false
        if (!vemo().vault.configured()) return false
        if (vemo().gate.isRecording()) { jobFinished(params, true); return false }
        vemo().sync { if (!stopped) jobFinished(params, false); schedule(this, 15 * 60 * 1000L) }
        return true
    }
    override fun onStopJob(params: JobParameters): Boolean { stopped = true; return true }
    companion object {
        fun schedule(context: Context, delay: Long = 3000L) {
            val app = context.vemo()
            if (!app.vault.configured() || app.store.list().none { it.uploadable && it.mode == Mode.DESKTOP }) return
            context.getSystemService(JobScheduler::class.java).schedule(JobInfo.Builder(8, ComponentName(context, SyncJobService::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY).setMinimumLatency(delay)
                .setBackoffCriteria(30_000, JobInfo.BACKOFF_POLICY_EXPONENTIAL).build())
        }
    }
}
