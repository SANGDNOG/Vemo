"""Pinned executable Mac profiles. Memory figures are estimates, not guarantees."""
import os
import platform

DEFAULT_SELECTION = {'asr': 'asr-qwen3-1.7b', 'notes': 'notes-qwen3.5-27b'}
PROFILES = {
    'asr-qwen3-1.7b': dict(role='asr', label='Qwen3 ASR 1.7B', storage='asr', repo_id='Qwen/Qwen3-ASR-1.7B', revision='7278e1e70fe206f11671096ffdd38061171dd6e5', backend='mlx', download_bytes_estimate=3500000000, memory_gb_estimate=8, quality_status='existing_default'),
    'asr-qwen3-0.6b': dict(role='asr', label='Qwen3 ASR 0.6B', storage='asr-qwen3-0.6b', repo_id='Qwen/Qwen3-ASR-0.6B', revision='5eb144179a02acc5e5ba31e748d22b0cf3e303b0', backend='mlx', download_bytes_estimate=1900000000, memory_gb_estimate=4, quality_status='metadata_verified_inference_unverified'),
    'notes-qwen3.5-27b': dict(role='notes', label='Qwen3.5 27B · 4-bit', storage='notes', repo_id='mlx-community/Qwen3.5-27B-4bit', revision='45797d2985a12c55e6473686e9ea91b95e959553', backend='mlx', download_bytes_estimate=17000000000, memory_gb_estimate=32, quality_status='existing_default'),
    'notes-qwen3-4b': dict(role='notes', label='Qwen3 4B · 4-bit', storage='title', repo_id='mlx-community/Qwen3-4B-Instruct-2507-4bit', revision='50d427756c6b1b2fe0c0a10f67fbda1fc8e82c1b', backend='mlx', download_bytes_estimate=2300000000, memory_gb_estimate=8, quality_status='synthetic_validation_failed', selectable=False),
}


def device_capabilities(*, system=None, machine=None, ram_bytes=None):
    system = system or platform.system(); machine = machine or platform.machine()
    if ram_bytes is None:
        try: ram_bytes = os.sysconf('SC_PHYS_PAGES') * os.sysconf('SC_PAGE_SIZE')
        except (ValueError, OSError, AttributeError): ram_bytes = None
    supported = system == 'Darwin' and machine in {'arm64', 'aarch64'}
    return dict(platform=system, architecture=machine, ram_bytes=ram_bytes,
                unified_memory=supported, mlx_supported=supported, gpu_vram_bytes=None)


def validate_selection(selection, device=None):
    if not isinstance(selection, dict) or set(selection) != {'asr', 'notes'}:
        raise ValueError('invalid_model_selection')
    for role, identifier in selection.items():
        if not isinstance(identifier, str) or identifier not in PROFILES: raise ValueError('unknown_model_profile')
        profile = PROFILES[identifier]
        if profile['role'] != role: raise ValueError('wrong_model_role')
        if not profile.get('selectable',True): raise ValueError('model_profile_incompatible')
        if profile['backend'] != 'mlx' or not (device or device_capabilities())['mlx_supported']:
            raise ValueError('unsupported_model_backend')
    return dict(selection)


def profile_storage(selection, role):
    if role not in DEFAULT_SELECTION: return role
    identifier = selection[role]
    if identifier not in PROFILES or PROFILES[identifier]['role'] != role: raise ValueError('wrong_model_role')
    return PROFILES[identifier]['storage']


def recommendations(device):
    ram = (device['ram_bytes'] or 0) / 1024**3
    return dict(device=device, asr=('asr-qwen3-1.7b' if ram >= 16 else 'asr-qwen3-0.6b') if device['mlx_supported'] else None,
                notes=('notes-qwen3.5-27b' if ram >= 32 else None) if device['mlx_supported'] else None,
                estimated=True, reason='Conservative total RAM estimate including context and OS headroom; models run sequentially. The 4B notes option failed synthetic schema/source-fidelity validation and is experimental; no notes recommendation below 32 GiB. No separate GPU VRAM is assumed on Apple silicon.')
