import { useState } from "react";
import TextField from "@mui/material/TextField";
import IconButton from "@mui/material/IconButton";
import InputAdornment from "@mui/material/InputAdornment";
import Visibility from "@mui/icons-material/Visibility";
import VisibilityOff from "@mui/icons-material/VisibilityOff";
import { useTranslation } from "react-i18next";

interface PasswordProps {
  pass: string;
  setPass: (value: string) => void;
}

const PasswordField = ({ pass, setPass }: PasswordProps) => {
  const { t } = useTranslation();
  const [showPassword, setShowPassword] = useState(false);

  return (
    <TextField
      label={t('common.password')}
      type={showPassword ? "text" : "password"}
      value={pass}
      onChange={(event) => setPass(event.target.value)}
      fullWidth
      size="small"
      InputProps={{
        endAdornment: (
          <InputAdornment position="end">
            <IconButton
              onClick={() => setShowPassword((prev) => !prev)}
              edge="end"
              size="small"
            >
              {showPassword ? <VisibilityOff /> : <Visibility />}
            </IconButton>
          </InputAdornment>
        )
      }}
    />
  );
};

export default PasswordField;