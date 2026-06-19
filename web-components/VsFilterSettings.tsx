import React, { FC, useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import Button from '@mui/material/Button';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Checkbox from '@mui/material/Checkbox';
import CircularProgress from '@mui/material/CircularProgress';
import Divider from '@mui/material/Divider';
import FormControl from '@mui/material/FormControl';
import FormControlLabel from '@mui/material/FormControlLabel';
import Grid from '@mui/material/Grid';
import InputLabel from '@mui/material/InputLabel';
import MenuItem from '@mui/material/MenuItem';
import Paper from '@mui/material/Paper';
import Select from '@mui/material/Select';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';

import { selectOptions } from '../../../utils/selectOptions';

interface VsFilterSettingsProps {
    onSave?: () => void;
}

type VsFilterPreset = 'anime-upscaled' | 'anime-interpolated' | 'custom' | 'upscale-only';

interface EncodingOptions {
    EnableVsFilterPipeline: boolean;
    VsFilterPreset: VsFilterPreset;
    VsFilterCustomScript: string;
    VsUpscaleModel: string;
    VsInterpolationModel: string;
    VsTargetWidth: number;
    VsTargetHeight: number;
    VsTargetFpsNum: number;
    VsTargetFpsDen: number;
    VsPixelFormat: string;
    VsThreads: number;
    VsEncoderArgs: string;
}

const VS_FILTER_PRESETS = [
    { value: 'anime-upscaled', label: 'Anime Upscaled (2x + HQ)' },
    { value: 'anime-interpolated', label: 'Anime Interpolated (2x + 60fps)' },
    { value: 'upscale-only', label: 'Upscale Only (2x)' },
    { value: 'custom', label: 'Custom Script' }
] as const;

const UPSCALE_MODELS = [
    { value: 'realesr-animevideov3', label: 'Real-ESRGAN AnimeVideo v3 (Recommended)' },
    { value: 'realesrgan-x4plus', label: 'Real-ESRGAN x4 Plus' },
    { value: 'realesrgan-x4plus-anime', label: 'Real-ESRGAN x4 Plus Anime' },
    { value: 'swinput', label: 'SwinIR (Generic)' }
] as const;

const INTERPOLATION_MODELS = [
    { value: 'rife-4.25', label: 'RIFE 4.25 (Recommended)' },
    { value: 'rife-4.16', label: 'RIFE 4.16' },
    { value: 'amtvcflow', label: 'AMT VcFlow' },
    { value: '', label: 'None (No Interpolation)' }
] as const;

const PIXEL_FORMATS = [
    { value: 'YUV420P10', label: 'YUV420P10 (10-bit, Recommended)' },
    { value: 'YUV420P8', label: 'YUV420P8 (8-bit)' },
    { value: 'YUV422P10', label: 'YUV422P10 (10-bit 4:2:2)' }
] as const;

const FPS_OPTIONS = [
    { num: 60000, den: 1001, label: '~60 fps (NTSC)' },
    { num: 30000, den: 1001, label: '~30 fps (NTSC)' },
    { num: 24000, den: 1001, label: '~24 fps (Film)' },
    { num: 50, den: 1, label: '50 fps (PAL)' }
] as const;

export const VsFilterSettings: FC<VsFilterSettingsProps> = ({ onSave }) => {
    const { t } = useTranslation();

    const [settings, setSettings] = useState<EncodingOptions>({
        EnableVsFilterPipeline: false,
        VsFilterPreset: 'anime-upscaled',
        VsFilterCustomScript: '',
        VsUpscaleModel: 'realesr-animevideov3',
        VsInterpolationModel: 'rife-4.25',
        VsTargetWidth: 3840,
        VsTargetHeight: 2160,
        VsTargetFpsNum: 60000,
        VsTargetFpsDen: 1001,
        VsPixelFormat: 'YUV420P10',
        VsThreads: 0,
        VsEncoderArgs: ''
    });

    const [isSaving, setIsSaving] = useState(false);

    const updateSetting = useCallback(<K extends keyof EncodingOptions>(
        key: K,
        value: EncodingOptions[K]
    ) => {
        setSettings(prev => ({ ...prev, [key]: value }));
    }, []);

    const handleSave = useCallback(async () => {
        setIsSaving(true);
        try {
            const response = await fetch('/EncodingOptions/VsFilter', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(settings)
            });
            if (response.ok) {
                onSave?.();
            }
        } finally {
            setIsSaving(false);
        }
    }, [settings, onSave]);

    const isCustomPreset = settings.VsFilterPreset === 'custom';
    const showInterpolation = settings.VsFilterPreset.includes('interpolat');

    return (
        <Paper sx={{ p: 3 }}>
            <Typography variant='h5' gutterBottom>
                4K×2 VapourSynth Filter Pipeline
            </Typography>
            <Typography variant='body2' color='text.secondary' sx={{ mb: 3 }}>
                Enable AI-powered upscaling and frame interpolation using FFmpeg's built-in VapourSynth filter.
                This provides higher quality transcoding than traditional methods with reduced overhead.
            </Typography>

            <Divider sx={{ my: 2 }} />

            <FormControlLabel
                control={
                    <Checkbox
                        checked={settings.EnableVsFilterPipeline}
                        onChange={(e) => updateSetting('EnableVsFilterPipeline', e.target.checked)}
                    />
                }
                label='Enable 4K×2 VS Filter Pipeline'
            />

            <Card sx={{ mt: 3 }}>
                <CardContent>
                    <Typography variant='h6' gutterBottom>
                        Preset Configuration
                    </Typography>

                    <Grid container spacing={3}>
                        <Grid item xs={12} md={6}>
                            <FormControl fullWidth>
                                <InputLabel>Quality Preset</InputLabel>
                                <Select
                                    value={settings.VsFilterPreset}
                                    label='Quality Preset'
                                    onChange={(e) => updateSetting('VsFilterPreset', e.target.value as VsFilterPreset)}
                                    disabled={!settings.EnableVsFilterPipeline}
                                >
                                    {selectOptions(VS_FILTER_PRESETS)}
                                </Select>
                            </FormControl>
                        </Grid>

                        <Grid item xs={12} md={6}>
                            <FormControl fullWidth>
                                <InputLabel>Upscaling Model</InputLabel>
                                <Select
                                    value={settings.VsUpscaleModel}
                                    label='Upscaling Model'
                                    onChange={(e) => updateSetting('VsUpscaleModel', e.target.value)}
                                    disabled={!settings.EnableVsFilterPipeline || isCustomPreset}
                                >
                                    {selectOptions(UPSCALE_MODELS)}
                                </Select>
                            </FormControl>
                        </Grid>

                        {showInterpolation && (
                            <Grid item xs={12} md={6}>
                                <FormControl fullWidth>
                                    <InputLabel>Interpolation Model</InputLabel>
                                    <Select
                                        value={settings.VsInterpolationModel}
                                        label='Interpolation Model'
                                        onChange={(e) => updateSetting('VsInterpolationModel', e.target.value)}
                                        disabled={!settings.EnableVsFilterPipeline || isCustomPreset}
                                    >
                                        {selectOptions(INTERPOLATION_MODELS)}
                                    </Select>
                                </FormControl>
                            </Grid>
                        )}
                    </Grid>
                </CardContent>
            </Card>

            <Card sx={{ mt: 3 }}>
                <CardContent>
                    <Typography variant='h6' gutterBottom>
                        Output Settings
                    </Typography>

                    <Grid container spacing={3}>
                        <Grid item xs={6} md={3}>
                            <TextField
                                fullWidth
                                type='number'
                                label='Target Width'
                                value={settings.VsTargetWidth}
                                onChange={(e) => updateSetting('VsTargetWidth', parseInt(e.target.value, 10) || 3840)}
                                disabled={!settings.EnableVsFilterPipeline || isCustomPreset}
                                inputProps={{ min: 1280, max: 7680 }}
                            />
                        </Grid>

                        <Grid item xs={6} md={3}>
                            <TextField
                                fullWidth
                                type='number'
                                label='Target Height'
                                value={settings.VsTargetHeight}
                                onChange={(e) => updateSetting('VsTargetHeight', parseInt(e.target.value, 10) || 2160)}
                                disabled={!settings.EnableVsFilterPipeline || isCustomPreset}
                                inputProps={{ min: 720, max: 4320 }}
                            />
                        </Grid>

                        <Grid item xs={12} md={6}>
                            <FormControl fullWidth>
                                <InputLabel>Target Framerate</InputLabel>
                                <Select
                                    value={`${settings.VsTargetFpsNum}/${settings.VsTargetFpsDen}`}
                                    label='Target Framerate'
                                    onChange={(e) => {
                                        const [num, den] = (e.target.value as string).split('/').map(Number);
                                        updateSetting('VsTargetFpsNum', num);
                                        updateSetting('VsTargetFpsDen', den);
                                    }}
                                    disabled={!settings.EnableVsFilterPipeline || isCustomPreset}
                                >
                                    {FPS_OPTIONS.map((fps) => (
                                        <MenuItem key={`${fps.num}/${fps.den}`} value={`${fps.num}/${fps.den}`}>
                                            {fps.label}
                                        </MenuItem>
                                    ))}
                                </Select>
                            </FormControl>
                        </Grid>

                        <Grid item xs={12} md={6}>
                            <FormControl fullWidth>
                                <InputLabel>Pixel Format</InputLabel>
                                <Select
                                    value={settings.VsPixelFormat}
                                    label='Pixel Format'
                                    onChange={(e) => updateSetting('VsPixelFormat', e.target.value)}
                                    disabled={!settings.EnableVsFilterPipeline || isCustomPreset}
                                >
                                    {selectOptions(PIXEL_FORMATS)}
                                </Select>
                            </FormControl>
                        </Grid>

                        <Grid item xs={12} md={6}>
                            <TextField
                                fullWidth
                                type='number'
                                label='Threads (0 = auto)'
                                value={settings.VsThreads}
                                onChange={(e) => updateSetting('VsThreads', parseInt(e.target.value, 10) || 0)}
                                disabled={!settings.EnableVsFilterPipeline}
                                inputProps={{ min: 0, max: 64 }}
                            />
                        </Grid>
                    </Grid>
                </CardContent>
            </Card>

            <Card sx={{ mt: 3 }}>
                <CardContent>
                    <Typography variant='h6' gutterBottom>
                        Custom FFmpeg Arguments
                    </Typography>
                    <Typography variant='body2' color='text.secondary' sx={{ mb: 2 }}>
                        Additional FFmpeg encoder arguments. Leave empty for defaults.
                        Example: <code>-preset fast -crf 18 -b:v 50M</code>
                    </Typography>
                    <TextField
                        fullWidth
                        label='Custom Encoder Args'
                        value={settings.VsEncoderArgs}
                        onChange={(e) => updateSetting('VsEncoderArgs', e.target.value)}
                        disabled={!settings.EnableVsFilterPipeline}
                        placeholder='-preset fast -crf 18 -b:v 50M'
                    />
                </CardContent>
            </Card>

            {isCustomPreset && (
                <Card sx={{ mt: 3 }}>
                    <CardContent>
                        <Typography variant='h6' gutterBottom>
                            Custom VapourSynth Script
                        </Typography>
                        <Typography variant='body2' color='text.secondary' sx={{ mb: 2 }}>
                            Enter your custom VapourSynth script. The script receives <code>clip</code> as input
                            and must call <code>clip.set_output()</code>.
                        </Typography>
                        <TextField
                            fullWidth
                            multiline
                            minRows={10}
                            maxRows={20}
                            label='VapourSynth Script'
                            value={settings.VsFilterCustomScript}
                            onChange={(e) => updateSetting('VsFilterCustomScript', e.target.value)}
                            disabled={!settings.EnableVsFilterPipeline}
                            placeholder={`import vapoursynth as vs
core = vs.core

# Your custom processing here
clip = clip.resize.Bicubic(width=3840, height=2160)

clip.set_output()`}
                        />
                    </CardContent>
                </Card>
            )}

            <Divider sx={{ my: 3 }} />

            <Button
                variant='contained'
                color='primary'
                onClick={handleSave}
                disabled={isSaving || !settings.EnableVsFilterPipeline}
                startIcon={isSaving ? <CircularProgress size={20} /> : undefined}
            >
                {isSaving ? 'Saving...' : 'Save Settings'}
            </Button>
        </Paper>
    );
};
